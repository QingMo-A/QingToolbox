package com.qingtoolbox.android

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.viewinterop.AndroidView

/**
 * Renders a loaded module.
 *
 * The screen only supplies the frame: the system file picker the module may ask for, and
 * the shell palette. Everything inside belongs to the module and stays on the shell's own
 * private storage, since the module runtime refuses any request that leaves the package.
 */
@Composable
fun MobileModuleWebView(
    session: MobileModuleSession,
    modifier: Modifier = Modifier,
) {
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        session.deliverPickedFile(uri)
    }

    DisposableEffect(session) {
        session.registerFilePicker { picker.launch(arrayOf("*/*")) }
        onDispose { session.registerFilePicker(null) }
    }

    val themeCss = rememberMobileModuleThemeCss()
    LaunchedEffect(session, themeCss) { session.applyTheme(themeCss) }

    AndroidView(
        modifier = modifier,
        factory = { context -> session.attach(context) },
    )
}

/**
 * Translates the active shell theme into the CSS custom properties the module stylesheet
 * reads, so a module follows the selected theme without shipping any theme code.
 *
 * Gradients and glows of the more decorative appearances are flattened to their solid
 * primary colour: a web page cannot inherit a Compose brush, and a solid accent keeps the
 * module readable in every appearance.
 */
@Composable
fun rememberMobileModuleThemeCss(): String {
    val scheme = MaterialTheme.colorScheme
    val appearance = LocalQingAppearance.current
    val density = LocalDensity.current
    return remember(scheme, appearance, density) {
        val radius = with(density) { appearance.cardCornerRadius.toPx() }.toInt().coerceIn(8, 24)
        val variables = linkedMapOf(
            "--qing-bg" to scheme.background,
            "--qing-surface" to scheme.surfaceContainerLow,
            "--qing-surface-2" to scheme.surfaceContainerHigh,
            "--qing-text" to scheme.onSurface,
            "--qing-text-dim" to scheme.onSurfaceVariant,
            "--qing-border" to appearance.cardBorderColor,
            "--qing-primary" to scheme.primary,
            "--qing-primary-soft" to lerp(scheme.background, scheme.primary, 0.16f),
            "--qing-on-primary" to scheme.onPrimary,
            "--qing-danger" to scheme.error,
        )
        buildString {
            append(":root{")
            variables.forEach { (name, color) ->
                append(name).append(':').append(color.cssHex()).append(';')
            }
            append("--qing-radius:").append(radius).append("px;")
            append("--qing-gap:").append(MODULE_THEME_GAP_DP).append("px;")
            append('}')
        }
    }
}

/** Spacing between stacked blocks inside a module page, in CSS pixels. */
private const val MODULE_THEME_GAP_DP = 12

private fun Color.cssHex(): String = "#%06X".format(0xFFFFFF and toArgb())
