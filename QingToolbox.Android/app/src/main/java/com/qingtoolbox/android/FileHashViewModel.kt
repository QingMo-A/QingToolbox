package com.qingtoolbox.android

import android.app.Application
import android.net.Uri
import android.provider.OpenableColumns
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

enum class FileHashPhase {
    EMPTY,
    LOADING_FILE,
    READY,
    CALCULATING,
    COMPLETE,
    ERROR,
}

data class FileHashUiState(
    val fileUri: Uri? = null,
    val fileName: String? = null,
    val fileSize: Long? = null,
    val selectedAlgorithms: Set<HashAlgorithm> = HashAlgorithm.entries.toSet(),
    val phase: FileHashPhase = FileHashPhase.EMPTY,
    val bytesRead: Long = 0L,
    val results: List<HashDigestResult> = emptyList(),
    val errorMessage: String? = null,
)

class FileHashViewModel(application: Application) : AndroidViewModel(application) {
    private val contentResolver = application.contentResolver
    private val _uiState = MutableStateFlow(FileHashUiState())
    private var calculationJob: Job? = null

    val uiState: StateFlow<FileHashUiState> = _uiState.asStateFlow()

    fun selectFile(uri: Uri) {
        calculationJob?.cancel()
        calculationJob = null
        _uiState.value = FileHashUiState(
            fileUri = uri,
            phase = FileHashPhase.LOADING_FILE,
        )

        viewModelScope.launch(Dispatchers.IO) {
            val metadata = runCatching { readMetadata(uri) }.getOrNull()
            _uiState.update { state ->
                if (state.fileUri != uri) return@update state
                state.copy(
                    fileName = metadata?.name
                        ?: uri.lastPathSegment
                        ?: getApplication<Application>().getString(R.string.file_hash_selected_file),
                    fileSize = metadata?.size,
                    phase = FileHashPhase.READY,
                    bytesRead = 0L,
                    results = emptyList(),
                    errorMessage = null,
                )
            }
        }
    }

    fun toggleAlgorithm(algorithm: HashAlgorithm) {
        _uiState.update { state ->
            if (state.phase == FileHashPhase.CALCULATING) return@update state
            val next = state.selectedAlgorithms.toMutableSet()
            if (!next.add(algorithm)) next.remove(algorithm)
            state.copy(selectedAlgorithms = next, errorMessage = null)
        }
    }

    fun calculate() {
        val state = _uiState.value
        val uri = state.fileUri ?: return
        val algorithms = HashAlgorithm.entries.filter { it in state.selectedAlgorithms }
        if (algorithms.isEmpty() || state.phase == FileHashPhase.CALCULATING) return

        calculationJob?.cancel()
        calculationJob = viewModelScope.launch(Dispatchers.IO) {
            _uiState.update { current ->
                if (current.fileUri == uri) {
                    current.copy(
                        phase = FileHashPhase.CALCULATING,
                        bytesRead = 0L,
                        results = emptyList(),
                        errorMessage = null,
                    )
                } else {
                    current
                }
            }

            try {
                val coroutineContext = currentCoroutineContext()
                val results = contentResolver.openInputStream(uri)?.use { input ->
                    StreamingDigest.calculate(
                        input = input,
                        algorithms = algorithms,
                        onProgress = { bytesRead ->
                            _uiState.update { current ->
                                if (current.fileUri == uri) {
                                    current.copy(bytesRead = bytesRead)
                                } else {
                                    current
                                }
                            }
                        },
                        shouldCancel = { !coroutineContext.isActive },
                    )
                } ?: throw IllegalStateException()

                _uiState.update { current ->
                    if (current.fileUri == uri) {
                        current.copy(
                            phase = FileHashPhase.COMPLETE,
                            results = results,
                            bytesRead = current.fileSize ?: current.bytesRead,
                            errorMessage = null,
                        )
                    } else {
                        current
                    }
                }
            } catch (_: CancellationException) {
                // A user cancellation or a new file selection owns the next state.
            } catch (error: SecurityException) {
                setError(uri, getApplication<Application>().getString(R.string.file_hash_permission_error), error)
            } catch (error: Exception) {
                val message = if (error is IllegalStateException) {
                    getApplication<Application>().getString(R.string.file_hash_open_error)
                } else {
                    getApplication<Application>().getString(R.string.file_hash_calculation_error)
                }
                setError(uri, message, error)
            }
        }
    }

    fun cancelCalculation() {
        calculationJob?.cancel()
        calculationJob = null
        _uiState.update { state ->
            state.copy(
                phase = if (state.fileUri == null) FileHashPhase.EMPTY else FileHashPhase.READY,
                bytesRead = 0L,
                results = emptyList(),
                errorMessage = null,
            )
        }
    }

    override fun onCleared() {
        calculationJob?.cancel()
        super.onCleared()
    }

    private fun setError(uri: Uri, message: String, cause: Exception) {
        if (cause is CancellationException) return
        _uiState.update { current ->
            if (current.fileUri == uri) {
                current.copy(
                    phase = FileHashPhase.ERROR,
                    errorMessage = message,
                    results = emptyList(),
                )
            } else {
                current
            }
        }
    }

    private fun readMetadata(uri: Uri): FileMetadata? {
        contentResolver.query(
            uri,
            arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE),
            null,
            null,
            null,
        )?.use { cursor ->
            if (!cursor.moveToFirst()) return null
            val nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
            val sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE)
            return FileMetadata(
                name = if (nameIndex >= 0) cursor.getString(nameIndex) else null,
                size = if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) {
                    cursor.getLong(sizeIndex).takeIf { it >= 0L }
                } else {
                    null
                },
            )
        }
        return null
    }

    private data class FileMetadata(
        val name: String?,
        val size: Long?,
    )
}
