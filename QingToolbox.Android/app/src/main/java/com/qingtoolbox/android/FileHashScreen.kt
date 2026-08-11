package com.qingtoolbox.android

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Calculate
import androidx.compose.material.icons.outlined.ContentCopy
import androidx.compose.material.icons.outlined.Description
import androidx.compose.material.icons.outlined.FolderOpen
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.compose.ui.platform.LocalContext
import kotlinx.coroutines.launch
import java.util.Locale

@Composable
fun FileHashScreen(
    modifier: Modifier = Modifier,
    viewModel: FileHashViewModel = viewModel(),
) {
    val state by viewModel.uiState.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val snackbarHostState = remember { SnackbarHostState() }
    val errorMessage = state.errorMessage
    val filePicker = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.OpenDocument(),
    ) { uri ->
        uri?.let(viewModel::selectFile)
    }

    Box(modifier = modifier.fillMaxSize()) {
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            item {
                FileSelectionCard(
                    state = state,
                    onChooseFile = { filePicker.launch(arrayOf("*/*")) },
                )
            }
            item {
                HashAlgorithmCard(
                    state = state,
                    onToggle = viewModel::toggleAlgorithm,
                )
            }
            item {
                HashActionCard(
                    state = state,
                    onCalculate = viewModel::calculate,
                    onCancel = viewModel::cancelCalculation,
                )
            }
            if (errorMessage != null) {
                item { HashErrorCard(message = errorMessage) }
            }
            if (state.results.isNotEmpty()) {
                item { FileHashSectionHeader(title = "Results") }
                items(state.results.size) { index ->
                    val result = state.results[index]
                    HashResultCard(
                        result = result,
                        onCopy = {
                            copyToClipboard(context, result)
                            scope.launch {
                                snackbarHostState.showSnackbar("${result.algorithm.label} copied")
                            }
                        },
                    )
                }
            }
            item {
                QingStatusText(
                    text = "Files stay on this device. Hashing uses the Android file picker and a streaming read.",
                    modifier = Modifier.padding(top = 4.dp, bottom = 20.dp),
                )
            }
        }
        SnackbarHost(
            hostState = snackbarHostState,
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(16.dp),
        )
    }
}

@Composable
private fun FileHashSectionHeader(title: String) {
    Text(
        text = title,
        style = MaterialTheme.typography.titleMedium,
        fontWeight = FontWeight.SemiBold,
        modifier = Modifier.padding(top = 4.dp),
    )
}

@Composable
private fun FileSelectionCard(
    state: FileHashUiState,
    onChooseFile: () -> Unit,
) {
    QingCard(modifier = Modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier.padding(18.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            QingIconSurface(modifier = Modifier.size(48.dp)) {
                Icon(
                    imageVector = Icons.Outlined.Description,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                )
            }
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = state.fileName ?: "No file selected",
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.SemiBold,
                )
                Text(
                    text = when (state.phase) {
                        FileHashPhase.LOADING_FILE -> "Loading file details..."
                        else -> formatBytes(state.fileSize)
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Spacer(Modifier.height(10.dp))
                QingSecondaryButton(onClick = onChooseFile) {
                    Icon(Icons.Outlined.FolderOpen, contentDescription = null)
                    Spacer(Modifier.size(8.dp))
                    Text("Choose file")
                }
            }
        }
    }
}

@Composable
private fun HashAlgorithmCard(
    state: FileHashUiState,
    onToggle: (HashAlgorithm) -> Unit,
) {
    QingCard(modifier = Modifier.fillMaxWidth()) {
        Column(modifier = Modifier.padding(vertical = 8.dp)) {
            Text(
                text = "Algorithms",
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.SemiBold,
                modifier = Modifier.padding(horizontal = 18.dp, vertical = 8.dp),
            )
            HashAlgorithm.entries.forEachIndexed { index, algorithm ->
                val checked = algorithm in state.selectedAlgorithms
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .toggleable(
                            value = checked,
                            enabled = state.phase != FileHashPhase.CALCULATING,
                            role = Role.Checkbox,
                            onValueChange = { onToggle(algorithm) },
                        )
                        .padding(horizontal = 10.dp, vertical = 2.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    QingCheckbox(
                        checked = checked,
                        onCheckedChange = null,
                        enabled = state.phase != FileHashPhase.CALCULATING,
                    )
                    Text(algorithm.label, style = MaterialTheme.typography.bodyLarge)
                }
                if (index < HashAlgorithm.entries.lastIndex) QingDivider(
                    modifier = Modifier.padding(horizontal = 18.dp),
                )
            }
        }
    }
}

@Composable
private fun HashActionCard(
    state: FileHashUiState,
    onCalculate: () -> Unit,
    onCancel: () -> Unit,
) {
    val calculating = state.phase == FileHashPhase.CALCULATING
    val canCalculate = state.fileUri != null &&
        state.selectedAlgorithms.isNotEmpty() &&
        state.phase != FileHashPhase.LOADING_FILE &&
        !calculating
    QingCard(modifier = Modifier.fillMaxWidth()) {
        Column(
            modifier = Modifier.padding(18.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            if (calculating) {
                val progress = state.fileSize?.takeIf { it > 0L }?.let { size ->
                    (state.bytesRead.toDouble() / size.toDouble()).coerceIn(0.0, 1.0).toFloat()
                }
                if (progress == null) {
                    LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                } else {
                    LinearProgressIndicator(
                        progress = { progress },
                        modifier = Modifier.fillMaxWidth(),
                    )
                }
                QingStatusText(
                    text = "Calculating: ${formatBytes(state.bytesRead)} / ${formatBytes(state.fileSize)}",
                )
                QingSecondaryButton(onClick = onCancel) {
                    Text("Cancel")
                }
            } else {
                QingPrimaryButton(
                    onClick = onCalculate,
                    enabled = canCalculate,
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Icon(Icons.Outlined.Calculate, contentDescription = null)
                    Spacer(Modifier.size(8.dp))
                    Text(if (state.phase == FileHashPhase.COMPLETE) "Calculate again" else "Calculate")
                }
                if (state.fileUri == null) {
                    Text(
                        text = "Choose a file, then select one or more algorithms.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                } else if (state.selectedAlgorithms.isEmpty()) {
                    Text(
                        text = "Select at least one algorithm to calculate.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
    }
}

@Composable
private fun HashErrorCard(message: String) {
    QingCard(modifier = Modifier.fillMaxWidth()) {
        Text(
            text = message,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.error,
            modifier = Modifier.padding(18.dp),
        )
    }
}

@Composable
private fun HashResultCard(
    result: HashDigestResult,
    onCopy: () -> Unit,
) {
    QingCard(modifier = Modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier.padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = result.algorithm.label,
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.SemiBold,
                )
                Spacer(Modifier.height(4.dp))
                QingStatusText(text = result.value)
            }
            QingSecondaryButton(onClick = onCopy) {
                Icon(Icons.Outlined.ContentCopy, contentDescription = "Copy ${result.algorithm.label}")
                Spacer(Modifier.size(6.dp))
                Text("Copy")
            }
        }
    }
}

private fun copyToClipboard(context: Context, result: HashDigestResult) {
    val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
    clipboard?.setPrimaryClip(
        ClipData.newPlainText("${result.algorithm.label} hash", result.value),
    )
}

private fun formatBytes(bytes: Long?): String {
    if (bytes == null || bytes < 0L) return "Size unavailable"
    if (bytes < 1024L) return "$bytes B"
    val units = arrayOf("KB", "MB", "GB", "TB")
    var value = bytes.toDouble()
    var unitIndex = -1
    while (value >= 1024.0 && unitIndex < units.lastIndex) {
        value /= 1024.0
        unitIndex++
    }
    return String.format(Locale.US, "%.1f %s", value, units[unitIndex])
}
