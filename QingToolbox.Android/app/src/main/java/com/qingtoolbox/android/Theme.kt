package com.qingtoolbox.android

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

private val QingDefaultLight = lightColorScheme(
    primary = Color(0xFF006C4C),
    onPrimary = Color.White,
    primaryContainer = Color(0xFF8AF8C5),
    onPrimaryContainer = Color(0xFF002115),
    secondary = Color(0xFF4D6357),
    onSecondary = Color.White,
    secondaryContainer = Color(0xFFCFE9D9),
    onSecondaryContainer = Color(0xFF092017),
    background = Color(0xFFF6FBF7),
    onBackground = Color(0xFF171D19),
    surface = Color(0xFFF6FBF7),
    onSurface = Color(0xFF171D19),
)

private val QingDefaultDark = darkColorScheme(
    primary = Color(0xFF6CDBAA),
    onPrimary = Color(0xFF003824),
    primaryContainer = Color(0xFF005137),
    onPrimaryContainer = Color(0xFF8AF8C5),
    secondary = Color(0xFFB3CCBD),
    onSecondary = Color(0xFF1E3529),
    secondaryContainer = Color(0xFF354B3F),
    onSecondaryContainer = Color(0xFFCFE9D9),
    background = Color(0xFF101510),
    onBackground = Color(0xFFE0E5E0),
    surface = Color(0xFF101510),
    onSurface = Color(0xFFE0E5E0),
)

private val NeonCircuitLight = lightColorScheme(
    primary = Color(0xFF006874),
    onPrimary = Color.White,
    primaryContainer = Color(0xFF97F0FF),
    onPrimaryContainer = Color(0xFF001F24),
    secondary = Color(0xFF4A6367),
    onSecondary = Color.White,
    secondaryContainer = Color(0xFFCDE7EB),
    onSecondaryContainer = Color(0xFF051F23),
    background = Color(0xFFF5FCFD),
    onBackground = Color(0xFF171D1E),
    surface = Color(0xFFF5FCFD),
    onSurface = Color(0xFF171D1E),
)

private val NeonCircuitDark = darkColorScheme(
    primary = Color(0xFF4CD8E8),
    onPrimary = Color(0xFF00363D),
    primaryContainer = Color(0xFF004F58),
    onPrimaryContainer = Color(0xFF97F0FF),
    secondary = Color(0xFFB1CBD0),
    onSecondary = Color(0xFF1C3337),
    secondaryContainer = Color(0xFF334A4E),
    onSecondaryContainer = Color(0xFFCDE7EB),
    background = Color(0xFF0C1415),
    onBackground = Color(0xFFDDE4E5),
    surface = Color(0xFF0C1415),
    onSurface = Color(0xFFDDE4E5),
)

private val GreenlineLight = lightColorScheme(
    primary = Color(0xFF426500),
    onPrimary = Color.White,
    primaryContainer = Color(0xFFB5F263),
    onPrimaryContainer = Color(0xFF122000),
    secondary = Color(0xFF596248),
    onSecondary = Color.White,
    secondaryContainer = Color(0xFFDDE7C3),
    onSecondaryContainer = Color(0xFF171E0B),
    background = Color(0xFFFAFBEF),
    onBackground = Color(0xFF1A1D14),
    surface = Color(0xFFFAFBEF),
    onSurface = Color(0xFF1A1D14),
)

private val GreenlineDark = darkColorScheme(
    primary = Color(0xFF9AD649),
    onPrimary = Color(0xFF203600),
    primaryContainer = Color(0xFF304D00),
    onPrimaryContainer = Color(0xFFB5F263),
    secondary = Color(0xFFC1CBA8),
    onSecondary = Color(0xFF2A331A),
    secondaryContainer = Color(0xFF414A30),
    onSecondaryContainer = Color(0xFFDDE7C3),
    background = Color(0xFF13150E),
    onBackground = Color(0xFFE3E5D8),
    surface = Color(0xFF13150E),
    onSurface = Color(0xFFE3E5D8),
)

private val AuroraFlowLight = lightColorScheme(
    primary = Color(0xFF465D91),
    onPrimary = Color.White,
    primaryContainer = Color(0xFFD9E2FF),
    onPrimaryContainer = Color(0xFF001945),
    secondary = Color(0xFF5B5D72),
    onSecondary = Color.White,
    secondaryContainer = Color(0xFFE0E1F9),
    onSecondaryContainer = Color(0xFF181A2C),
    background = Color(0xFFFAF8FF),
    onBackground = Color(0xFF1A1B20),
    surface = Color(0xFFFAF8FF),
    onSurface = Color(0xFF1A1B20),
)

private val AuroraFlowDark = darkColorScheme(
    primary = Color(0xFFB0C6FF),
    onPrimary = Color(0xFF142E60),
    primaryContainer = Color(0xFF2E4678),
    onPrimaryContainer = Color(0xFFD9E2FF),
    secondary = Color(0xFFC4C5DD),
    onSecondary = Color(0xFF2D2E42),
    secondaryContainer = Color(0xFF434559),
    onSecondaryContainer = Color(0xFFE0E1F9),
    background = Color(0xFF111318),
    onBackground = Color(0xFFE3E2E9),
    surface = Color(0xFF111318),
    onSurface = Color(0xFFE3E2E9),
)

private val QingNovaLight = lightColorScheme(
    primary = Color(0xFF7A4A7E),
    onPrimary = Color.White,
    primaryContainer = Color(0xFFFFD7F8),
    onPrimaryContainer = Color(0xFF300936),
    secondary = Color(0xFF76566A),
    onSecondary = Color.White,
    secondaryContainer = Color(0xFFFFD9EB),
    onSecondaryContainer = Color(0xFF2C1221),
    background = Color(0xFFFFF7FB),
    onBackground = Color(0xFF211A1F),
    surface = Color(0xFFFFF7FB),
    onSurface = Color(0xFF211A1F),
)

private val QingNovaDark = darkColorScheme(
    primary = Color(0xFFEDB2E7),
    onPrimary = Color(0xFF472047),
    primaryContainer = Color(0xFF603663),
    onPrimaryContainer = Color(0xFFFFD7F8),
    secondary = Color(0xFFE4BCCD),
    onSecondary = Color(0xFF422736),
    secondaryContainer = Color(0xFF5B3D4C),
    onSecondaryContainer = Color(0xFFFFD9EB),
    background = Color(0xFF191216),
    onBackground = Color(0xFFEDE1E7),
    surface = Color(0xFF191216),
    onSurface = Color(0xFFEDE1E7),
)

@Composable
fun QingToolboxTheme(
    appearance: AppearanceTheme,
    content: @Composable () -> Unit,
) {
    val darkTheme = isSystemInDarkTheme()
    val colors = when (appearance) {
        AppearanceTheme.QING_DEFAULT -> if (darkTheme) QingDefaultDark else QingDefaultLight
        AppearanceTheme.NEON_CIRCUIT -> if (darkTheme) NeonCircuitDark else NeonCircuitLight
        AppearanceTheme.GREENLINE -> if (darkTheme) GreenlineDark else GreenlineLight
        AppearanceTheme.AURORA_FLOW -> if (darkTheme) AuroraFlowDark else AuroraFlowLight
        AppearanceTheme.QING_NOVA -> if (darkTheme) QingNovaDark else QingNovaLight
    }

    MaterialTheme(
        colorScheme = colors,
        typography = Typography(),
        content = content,
    )
}
