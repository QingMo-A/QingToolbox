package com.qingtoolbox.android

import android.app.Application
import android.content.Context
import android.content.SharedPreferences
import android.net.Uri
import androidx.annotation.StringRes
import androidx.lifecycle.AndroidViewModel
import java.util.Locale
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * One selectable appearance.
 *
 * A theme carries only its persisted [id] and its [labelRes]. What a theme looks like is
 * expressed by the theme itself — the applied colour scheme plus the swatch in the
 * picker — rather than by a written description, so the picker stays a list of choices.
 */
enum class AppearanceTheme(
    val id: String,
    @StringRes val labelRes: Int,
) {
    QING_DEFAULT("qing-default", R.string.theme_qing_default),
    NEON_CIRCUIT("neon-circuit", R.string.theme_neon_circuit),
    GREENLINE("greenline", R.string.theme_greenline),
    AURORA_FLOW("aurora-flow", R.string.theme_aurora_flow),
    QING_NOVA("qing-nova", R.string.theme_qing_nova),
    ;

    companion object {
        fun fromId(id: String?): AppearanceTheme =
            entries.firstOrNull { it.id == id } ?: QING_DEFAULT
    }
}

/** A short-lived notice for the shell, already resolved to text. */
data class ShellMessage(val text: String, val token: Long)

data class QingToolboxUiState(
    val appearance: AppearanceTheme = AppearanceTheme.QING_DEFAULT,
    val modules: List<InstalledMobileModule> = emptyList(),
    val loadedIds: Set<String> = emptySet(),
    val query: String = "",
    val filter: MobileModuleFilter = MobileModuleFilter.ALL,
    val busy: Boolean = false,
    val message: ShellMessage? = null,
)

private class AppearancePreferenceStore(context: Context) {
    private val preferences: SharedPreferences = context.getSharedPreferences(
        "qingtoolbox.mobile.settings",
        Context.MODE_PRIVATE,
    )

    fun read(): AppearanceTheme = try {
        AppearanceTheme.fromId(preferences.getString(APPEARANCE_KEY, null))
    } catch (_: RuntimeException) {
        AppearanceTheme.QING_DEFAULT
    }

    fun write(theme: AppearanceTheme) {
        runCatching {
            preferences.edit().putString(APPEARANCE_KEY, theme.id).apply()
        }
    }

    private companion object {
        const val APPEARANCE_KEY = "appearance"
    }
}

/**
 * The shell's state.
 *
 * Installed modules come from disk and loaded modules come from the runtime, and the two are
 * never conflated: importing a package leaves it unloaded, which is exactly what the modules
 * page reports until the user loads it.
 */
class QingToolboxViewModel(application: Application) : AndroidViewModel(application) {
    private val preferences = AppearancePreferenceStore(application.applicationContext)
    private val store = MobileModuleStore(application)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val _uiState = MutableStateFlow(
        QingToolboxUiState(
            appearance = preferences.read(),
            modules = store.listInstalled(),
        ),
    )

    val uiState: StateFlow<QingToolboxUiState> = _uiState.asStateFlow()

    /** Runtime instances of every loaded module, owned for the lifetime of the shell. */
    val runtime = MobileModuleRuntime(application)

    private var messageToken = 0L

    fun selectAppearance(theme: AppearanceTheme) {
        if (_uiState.value.appearance == theme) return
        _uiState.update { it.copy(appearance = theme) }
        preferences.write(theme)
    }

    fun setQuery(query: String) {
        _uiState.update { it.copy(query = query) }
    }

    fun setFilter(filter: MobileModuleFilter) {
        _uiState.update { it.copy(filter = filter) }
    }

    fun moduleFor(id: String?): InstalledMobileModule? =
        id?.let { wanted -> _uiState.value.modules.firstOrNull { it.id == wanted } }

    fun consumeMessage(token: Long) {
        _uiState.update { state ->
            if (state.message?.token == token) state.copy(message = null) else state
        }
    }

    /**
     * Reads a picked `.qmod` and installs it.
     *
     * The module is installed unloaded on purpose: importing a package must never start
     * executing it. A package that replaces an installed module first unloads the old
     * runtime, so nothing keeps running over a directory that just changed underneath it.
     */
    fun importModule(uri: Uri) {
        if (_uiState.value.busy) return
        _uiState.update { it.copy(busy = true) }
        scope.launch {
            val result = runCatching {
                val bytes = withContext(Dispatchers.IO) { store.readPackagedBytes(uri) }
                runCatching { MobileModuleArchive.read(bytes).manifest.id }
                    .getOrNull()
                    ?.let { id -> if (runtime.isLoaded(id)) runtime.unload(id) }
                withContext(Dispatchers.IO) { store.install(bytes) }
            }
            val message = result.fold(
                onSuccess = { install ->
                    refresh()
                    val name = install.module.displayName(currentLanguageTag())
                    string(
                        if (install.replaced) R.string.modules_import_replaced else R.string.modules_import_added,
                        name,
                    )
                },
                onFailure = ::describe,
            )
            _uiState.update { it.copy(busy = false, message = nextMessage(message)) }
        }
    }

    fun deleteModule(id: String) {
        val module = moduleFor(id) ?: return
        scope.launch {
            val result = runCatching {
                runtime.unload(id)
                withContext(Dispatchers.IO) { store.delete(module) }
            }
            val message = result.fold(
                onSuccess = {
                    refresh()
                    string(R.string.modules_deleted, module.displayName(currentLanguageTag()))
                },
                onFailure = ::describe,
            )
            _uiState.update { it.copy(message = nextMessage(message)) }
        }
    }

    fun loadModule(id: String) {
        val module = moduleFor(id) ?: return
        runtime.load(module)
        syncLoadedModules()
    }

    fun unloadModule(id: String) {
        runtime.unload(id)
        syncLoadedModules()
    }

    fun refresh() {
        val modules = store.listInstalled()
        val installedIds = modules.map { it.id }.toSet()
        // A module removed outside the shell must not keep a runtime behind.
        runtime.loadedIds.filterNot { it in installedIds }.forEach { runtime.unload(it) }
        _uiState.update { it.copy(modules = modules, loadedIds = runtime.loadedIds) }
    }

    override fun onCleared() {
        runtime.unloadAll()
        scope.cancel()
        super.onCleared()
    }

    private fun syncLoadedModules() {
        _uiState.update { it.copy(loadedIds = runtime.loadedIds) }
    }

    private fun currentLanguageTag(): String {
        val locales = getApplication<Application>().resources.configuration.locales
        val locale = if (locales.isEmpty) Locale.getDefault() else locales[0]
        return locale.toLanguageTag()
    }

    private fun describe(failure: Throwable): String = when (failure) {
        is MobileModuleFormatException -> string(failure.error.labelRes)
        else -> string(R.string.modules_operation_failed)
    }

    private fun string(@StringRes id: Int, vararg args: Any): String =
        getApplication<Application>().getString(id, *args)

    private fun nextMessage(text: String): ShellMessage {
        messageToken++
        return ShellMessage(text, messageToken)
    }
}
