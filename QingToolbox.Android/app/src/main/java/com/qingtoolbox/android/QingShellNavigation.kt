package com.qingtoolbox.android

import androidx.compose.animation.EnterTransition
import androidx.compose.animation.ExitTransition

/**
 * Navigation animation policy for the shell.
 *
 * The shell switches destinations with no transition. The default cross-fade was not just
 * slow to look at: Compose keeps the outgoing screen composed for the whole transition, so
 * a bottom-bar tap left the previous page painted underneath the incoming one instead of
 * replacing it. Removing the transition is also what the platform's own top-level
 * navigation does.
 *
 * These are passed explicitly to `NavHost` rather than left to the default, so the intent
 * is visible at the call site.
 */
internal val ShellEnterTransition: EnterTransition = EnterTransition.None

internal val ShellExitTransition: ExitTransition = ExitTransition.None
