package com.qingtoolbox.android

import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
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
    METAL,
    AURORA,
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
    /**
     * Ink for content sitting on [primaryGradient].
     *
     * This cannot be `MaterialTheme.colorScheme.onPrimary`. `onPrimary` is defined as the
     * ink for a background painted in `primary`, but a gradient is not `primary` — it is a
     * separate, usually much brighter ramp. In a dark scheme `onPrimary` is a dark tint,
     * so using it here produced grey text on bright silver and on the aurora sweep: text
     * that read as dirty next to the surface it sat on. Each theme states the ink its own
     * gradient needs instead, and themes without a gradient never read this.
     */
    val gradientContentColor: Color,
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
            gradientContentColor = Color.White,
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
            gradientContentColor = colors.onPrimary,
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
            gradientContentColor = Color.White,
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
            gradientContentColor = Color.White,
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
            gradientContentColor = Color.White,
            glowColor = Color(0xFF6C9DFF),
            glowStrength = 0.12f,
            navigationStyle = QingNavigationStyle.SOFT,
            technicalMonospaceEnabled = false,
        )

        AppearanceTheme.QING_NOVA -> QingAppearanceStyle(
            controlCornerRadius = 11.dp,
            cardCornerRadius = 11.dp,
            borderWidth = 1.dp,
            cardBorderColor = Color(0xFF5E8CC7).copy(alpha = 0.54f),
            controlBorderColor = Color(0xFF78D9DD).copy(alpha = 0.68f),
            cardElevation = 1.dp,
            controlElevation = 1.dp,
            pressedEmphasis = 0.14f,
            hoverEmphasis = 0.10f,
            focusEmphasis = 0.22f,
            primaryGradient = listOf(
                Color(0xFF52E6D2),
                Color(0xFF4C9AFF),
                Color(0xFF7587E6),
            ),
            usePrimaryGradient = true,
            gradientContentColor = Color.White,
            glowColor = Color(0xFF4C9AFF),
            glowStrength = 0.06f,
            navigationStyle = QingNavigationStyle.NOVA,
            technicalMonospaceEnabled = true,
        )

        // Brushed metal is defined by its edges, not its fill: a machined panel reads as
        // metal because of the crisp bevel and the touching shadow under it. Hence the small
        // radius, the strong border and the extra elevation, with no gradient wash. The type
        // is monospace because the material it imitates is label-printed hardware.
        AppearanceTheme.BRUSHED_METAL -> QingAppearanceStyle(
            controlCornerRadius = 8.dp,
            cardCornerRadius = 10.dp,
            borderWidth = 1.dp,
            cardBorderColor = Color(0xFF8B95A1).copy(alpha = 0.62f),
            controlBorderColor = Color(0xFF8994A2).copy(alpha = 0.88f),
            cardElevation = 3.dp,
            controlElevation = 3.dp,
            pressedEmphasis = 0.16f,
            hoverEmphasis = 0.10f,
            focusEmphasis = 0.22f,
            primaryGradient = listOf(
                Color(0xFFF8FAFC),
                Color(0xFFBDC6D0),
                Color(0xFF9AA5B0),
            ),
            usePrimaryGradient = true,
            // A bright silver ramp: dark ink is the only thing that stays legible on it, and
            // the dark tint `onPrimary` resolves to in a dark scheme is what produced the
            // grey text this replaced.
            gradientContentColor = Color(0xFF1C2530),
            glowColor = Color.Transparent,
            glowStrength = 0f,
            navigationStyle = QingNavigationStyle.METAL,
            technicalMonospaceEnabled = true,
        )

        // Aurora is the only theme whose identity is a moving gradient. The radius is large
        // and the border nearly invisible so nothing competes with the sweep, and there is a
        // glow to keep the gradient reading as light rather than paint.
        AppearanceTheme.AURORA -> QingAppearanceStyle(
            controlCornerRadius = 14.dp,
            cardCornerRadius = 16.dp,
            borderWidth = 1.dp,
            cardBorderColor = Color(0xFF7D8CD8).copy(alpha = 0.34f),
            controlBorderColor = Color(0xFF9BB8F0).copy(alpha = 0.46f),
            cardElevation = 2.dp,
            controlElevation = 1.dp,
            pressedEmphasis = 0.10f,
            hoverEmphasis = 0.08f,
            focusEmphasis = 0.18f,
            primaryGradient = listOf(
                Color(0xFF23D5AB),
                Color(0xFF23A6D5),
                Color(0xFF6F63FF),
                Color(0xFFE73C7E),
            ),
            usePrimaryGradient = true,
            gradientContentColor = Color.White,
            glowColor = Color(0xFF6F63FF),
            glowStrength = 0.18f,
            navigationStyle = QingNavigationStyle.AURORA,
            technicalMonospaceEnabled = false,
        )
    }
}

fun QingAppearanceStyle.primaryBrush(): Brush? =
    if (usePrimaryGradient && primaryGradient.size > 1) {
        Brush.linearGradient(primaryGradient)
    } else {
        null
    }

/**
 * Ink for content drawn on this style's gradient, or on the flat `primaryContainer` when it
 * has none.
 *
 * Both cases are the "filled with the accent" surface, so both are answered here rather than
 * at each call site: they were previously resolved inline next to every hero card, which is
 * how the primary button ended up reaching for `onPrimary` and painting grey on silver.
 */
@Composable
fun QingAppearanceStyle.contentColorOnAccent(): Color =
    if (primaryBrush() != null) gradientContentColor else MaterialTheme.colorScheme.onPrimaryContainer

/**
 * Black or white, whichever stays legible on [background].
 *
 * Used where the fill is an arbitrary accent rather than one of the scheme's roles — the
 * appearance swatches. No role colour is guaranteed to contrast with those, so the choice is
 * derived from the colour itself. The weights are the standard perceptual ones: the eye
 * reads green as much brighter than blue at equal luminance, and a flat average would put
 * white on mid-greens where it does not belong.
 */
fun inkOn(background: Color): Color {
    val luminance = 0.299f * background.red + 0.587f * background.green + 0.114f * background.blue
    return if (luminance > 0.6f) Color(0xFF1A1C1E) else Color.White
}

/**
 * Representative accent of an appearance, used wherever the theme has to be shown as a
 * colour rather than applied — swatches in the appearance picker.
 */
val AppearanceTheme.swatchColor: Color
    get() = when (this) {
        AppearanceTheme.QING_DEFAULT -> Color(0xFF006C4C)
        AppearanceTheme.NEON_CIRCUIT -> Color(0xFF006874)
        AppearanceTheme.GREENLINE -> Color(0xFF426500)
        AppearanceTheme.AURORA_FLOW -> Color(0xFF465D91)
        AppearanceTheme.QING_NOVA -> Color(0xFF4C9AFF)
        AppearanceTheme.BRUSHED_METAL -> Color(0xFF9AA5B0)
        AppearanceTheme.AURORA -> Color(0xFF6F63FF)
    }
