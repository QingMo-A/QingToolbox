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

    @Test
    fun everyThemeCarriesADistinctPersistedId() {
        // A duplicate id would silently make one theme unreachable: `fromId` returns the
        // first match, so the later theme could never be selected.
        val ids = AppearanceTheme.entries.map { it.id }
        assertEquals(ids.size, ids.toSet().size)
    }

    @Test
    fun additionsDoNotRenumberExistingThemes() {
        // These ids are written to SharedPreferences. Renaming or reordering them would
        // reset every user back to the default on upgrade, so the original five are pinned.
        assertEquals("qing-default", AppearanceTheme.QING_DEFAULT.id)
        assertEquals("neon-circuit", AppearanceTheme.NEON_CIRCUIT.id)
        assertEquals("greenline", AppearanceTheme.GREENLINE.id)
        assertEquals("aurora-flow", AppearanceTheme.AURORA_FLOW.id)
        assertEquals("qing-nova", AppearanceTheme.QING_NOVA.id)
    }
}
