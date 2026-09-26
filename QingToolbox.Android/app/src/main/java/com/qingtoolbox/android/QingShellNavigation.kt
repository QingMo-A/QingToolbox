package com.qingtoolbox.android

/**
 * Navigation animation policy for the shell.
 *
 * [SHELL_STAGGER_MILLIS] used to make a destination change feel like a slow dissolve.
 * Compose keeps the outgoing screen composed for the whole transition, so at 300 ms
 * a bottom-bar tap left the previous page painted underneath the new one while it
 * faded — the previous content was still visibly there, which read as lag rather than
 * polish. The shell now switches screens with no transition at all, which is what
 * every other tab bar on the platform does (Material's own NavigationBar guidance
 * drops the cross-fade between top-level destinations for exactly this reason).
 *
 * Kept as a named constant so the value is stated in one place rather than implied by
 * its absence, and so [QingShellNavigationTest] can pin it.
 */
internal const val SHELL_STAGGER_MILLIS = 0L

/**
 * Returns [startDestination] unchanged.
 *
 * The shell keeps this as an explicit hook rather than dropping the argument at the
 * call site: if a background colour is ever needed to hide a flash on the very first
 * frame, this is the single place to supply one, and the nav graph stays untouched.
 */
internal fun shellLaunchBackground(startDestination: Any): Any = startDestination
