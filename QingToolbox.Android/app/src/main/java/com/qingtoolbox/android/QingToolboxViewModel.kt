package com.qingtoolbox.android

import android.app.Application
import android.content.Context
import android.content.SharedPreferences
import androidx.lifecycle.AndroidViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

enum class AppearanceTheme(
    val id: String,
    val label: String,
    val description: String,
) {
    QING_DEFAULT("qing-default", "Qing Default", "Calm teal with a clear, everyday surface"),
    NEON_CIRCUIT("neon-circuit", "Neon Circuit", "High-contrast cyan and electric green"),
    GREENLINE("greenline", "Greenline", "Focused green accents for a practical workspace"),
    AURORA_FLOW("aurora-flow", "Aurora Flow", "Cool blue-violet tones with a soft glow"),
    QING_NOVA("qing-nova", "Qing Nova", "Deep blue-green with a focused technical glow"),
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
