package com.qingtoolbox.android

import android.app.Application
import android.content.Context
import android.content.SharedPreferences
import androidx.annotation.StringRes
import androidx.lifecycle.AndroidViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

enum class AppearanceTheme(
    val id: String,
    @StringRes val labelRes: Int,
    @StringRes val descriptionRes: Int,
) {
    QING_DEFAULT("qing-default", R.string.theme_qing_default, R.string.theme_qing_default_description),
    NEON_CIRCUIT("neon-circuit", R.string.theme_neon_circuit, R.string.theme_neon_circuit_description),
    GREENLINE("greenline", R.string.theme_greenline, R.string.theme_greenline_description),
    AURORA_FLOW("aurora-flow", R.string.theme_aurora_flow, R.string.theme_aurora_flow_description),
    QING_NOVA("qing-nova", R.string.theme_qing_nova, R.string.theme_qing_nova_description),
    ;

    companion object {
        fun fromId(id: String?): AppearanceTheme =
            entries.firstOrNull { it.id == id } ?: QING_DEFAULT
    }
}

data class QingToolboxUiState(
    val appearance: AppearanceTheme = AppearanceTheme.QING_DEFAULT,
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

class QingToolboxViewModel(application: Application) : AndroidViewModel(application) {
    private val preferences = AppearancePreferenceStore(application.applicationContext)
    private val _uiState = MutableStateFlow(QingToolboxUiState(appearance = preferences.read()))

    val uiState: StateFlow<QingToolboxUiState> = _uiState.asStateFlow()

    fun selectAppearance(theme: AppearanceTheme) {
        if (_uiState.value.appearance == theme) return
        _uiState.update { it.copy(appearance = theme) }
        preferences.write(theme)
    }
}
