package com.qingtoolbox.android

import org.junit.Assert.assertEquals
import org.junit.Test

class AppearanceThemeTest {
    @Test
    fun unknownPreferenceFallsBackToQingDefault() {
        assertEquals(
            AppearanceTheme.QING_DEFAULT,
            AppearanceTheme.fromId("damaged-preference"),
        )
        assertEquals(AppearanceTheme.QING_DEFAULT, AppearanceTheme.fromId(null))
    }

    @Test
    fun everyThemeHasStablePersistedId() {
        AppearanceTheme.entries.forEach { theme ->
            assertEquals(theme, AppearanceTheme.fromId(theme.id))
        }
    }
}
