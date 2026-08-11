package com.qingtoolbox.android

import androidx.compose.material3.ColorScheme
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

enum class QingNavigationStyle {
    STANDARD,
    CIRCUIT,
    TERMINAL,
    SOFT,
    NOVA,
}

@Immutable
data class QingAppearanceStyle(
    val controlCornerRadius: Dp,
    val cardCornerRadius: Dp,
    val borderWidth: Dp,
    val cardBorderColor: Color,
    val controlBorderColor: Color,
    val cardElevation: Dp,
    val controlElevation: Dp,
    val pressedEmphasis: Float,
    val hoverEmphasis: Float,
    val focusEmphasis: Float,
    val primaryGradient: List<Color>,
    val usePrimaryGradient: Boolean,
    val glowColor: Color,
    val glowStrength: Float,
    val navigationStyle: QingNavigationStyle,
    val technicalMonospaceEnabled: Boolean,
) {
    companion object {
        val Default = QingAppearanceStyle(
            controlCornerRadius = 12.dp,
            cardCornerRadius = 12.dp,
            borderWidth = 1.dp,
            cardBorderColor = Color(0x334D6357),
            controlBorderColor = Color(0x664D6357),
            cardElevation = 1.dp,
            controlElevation = 1.dp,
            pressedEmphasis = 0.12f,
            hoverEmphasis = 0.08f,
            focusEmphasis = 0.20f,
            primaryGradient = listOf(Color(0xFF006C4C)),
            usePrimaryGradient = false,
            glowColor = Color.Transparent,
            glowStrength = 0f,
            navigationStyle = QingNavigationStyle.STANDARD,
            technicalMonospaceEnabled = false,
        )
    }
}

val LocalQingAppearance = staticCompositionLocalOf { QingAppearanceStyle.Default }

internal fun qingAppearanceStyle(
    appearance: AppearanceTheme,
    colors: ColorScheme,
): QingAppearanceStyle {
    val primary = colors.primary
    val secondary = colors.secondary
    val outline = colors.outlineVariant
    return when (appearance) {
        AppearanceTheme.QING_DEFAULT -> QingAppearanceStyle(
            controlCornerRadius = 12.dp,
            cardCornerRadius = 12.dp,
            borderWidth = 1.dp,
            cardBorderColor = outline.copy(alpha = 0.48f),
            controlBorderColor = secondary.copy(alpha = 0.62f),
            cardElevation = 1.dp,
            controlElevation = 1.dp,
            pressedEmphasis = 0.12f,
            hoverEmphasis = 0.08f,
            focusEmphasis = 0.20f,
            primaryGradient = listOf(primary),
            usePrimaryGradient = false,
            glowColor = Color.Transparent,
            glowStrength = 0f,
            navigationStyle = QingNavigationStyle.STANDARD,
            technicalMonospaceEnabled = false,
        )

        AppearanceTheme.NEON_CIRCUIT -> QingAppearanceStyle(
            controlCornerRadius = 8.dp,
            cardCornerRadius = 8.dp,
            borderWidth = 1.dp,
            cardBorderColor = primary.copy(alpha = 0.48f),
            controlBorderColor = primary.copy(alpha = 0.78f),
            cardElevation = 0.dp,
            controlElevation = 1.dp,
            pressedEmphasis = 0.20f,
            hoverEmphasis = 0.14f,
            focusEmphasis = 0.28f,
            primaryGradient = listOf(primary, Color(0xFF0087FF)),
            usePrimaryGradient = true,
            glowColor = primary,
            glowStrength = 0.14f,
            navigationStyle = QingNavigationStyle.CIRCUIT,
            technicalMonospaceEnabled = false,
        )

        AppearanceTheme.GREENLINE -> QingAppearanceStyle(
            controlCornerRadius = 7.dp,
            cardCornerRadius = 7.dp,
            borderWidth = 1.dp,
            cardBorderColor = primary.copy(alpha = 0.42f),
            controlBorderColor = primary.copy(alpha = 0.72f),
            cardElevation = 0.dp,
            controlElevation = 0.dp,
            pressedEmphasis = 0.18f,
            hoverEmphasis = 0.10f,
            focusEmphasis = 0.24f,
            primaryGradient = listOf(primary, Color(0xFFB5F263)),
            usePrimaryGradient = false,
            glowColor = primary,
            glowStrength = 0.06f,
            navigationStyle = QingNavigationStyle.TERMINAL,
            technicalMonospaceEnabled = true,
        )

        AppearanceTheme.AURORA_FLOW -> QingAppearanceStyle(
            controlCornerRadius = 16.dp,
            cardCornerRadius = 18.dp,
            borderWidth = 1.dp,
            cardBorderColor = primary.copy(alpha = 0.28f),
            controlBorderColor = secondary.copy(alpha = 0.52f),
            cardElevation = 2.dp,
            controlElevation = 1.dp,
            pressedEmphasis = 0.10f,
            hoverEmphasis = 0.08f,
            focusEmphasis = 0.18f,
            primaryGradient = listOf(
                Color(0xFF52E1CE),
                Color(0xFF5C8EFF),
                Color(0xFF9B7BFF),
            ),
            usePrimaryGradient = true,
            glowColor = Color(0xFF6C9DFF),
            glowStrength = 0.12f,
            navigationStyle = QingNavigationStyle.SOFT,
            technicalMonospaceEnabled = false,
        )

        AppearanceTheme.QING_NOVA -> QingAppearanceStyle(
            controlCornerRadius = 11.dp,
            cardCornerRadius = 11.dp,
            borderWidth = 1.dp,
            cardBorderColor = Color(0xFF9F92FF).copy(alpha = 0.56f),
            controlBorderColor = Color(0xFF78D9DD).copy(alpha = 0.68f),
            cardElevation = 1.dp,
            controlElevation = 1.dp,
            pressedEmphasis = 0.14f,
            hoverEmphasis = 0.10f,
            focusEmphasis = 0.22f,
            primaryGradient = listOf(
                Color(0xFF52E6D2),
                Color(0xFF4C9AFF),
                Color(0xFF9C7CFF),
            ),
            usePrimaryGradient = true,
            glowColor = Color(0xFF718DFF),
            glowStrength = 0.10f,
            navigationStyle = QingNavigationStyle.NOVA,
            technicalMonospaceEnabled = true,
        )
    }
}

fun QingAppearanceStyle.primaryBrush(): Brush? =
    if (usePrimaryGradient && primaryGradient.size > 1) {
        Brush.linearGradient(primaryGradient)
    } else {
        null
    }
