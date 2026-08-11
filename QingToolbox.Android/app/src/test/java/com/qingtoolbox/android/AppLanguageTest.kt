package com.qingtoolbox.android

import org.junit.Assert.assertEquals
import org.junit.Test

class AppLanguageTest {
    @Test
    fun mapsSupportedTagsAndFollowSystemToTheExpectedOption() {
        assertEquals(AppLanguage.FOLLOW_SYSTEM, AppLanguage.fromTags(null))
        assertEquals(AppLanguage.FOLLOW_SYSTEM, AppLanguage.fromTags(""))
        assertEquals(AppLanguage.FOLLOW_SYSTEM, AppLanguage.fromTags("   "))
        assertEquals(AppLanguage.FOLLOW_SYSTEM.tag, "")
        assertEquals(AppLanguage.ENGLISH, AppLanguage.fromTags("en"))
        assertEquals(AppLanguage.ENGLISH, AppLanguage.fromTags("en-US"))
        assertEquals(AppLanguage.SIMPLIFIED_CHINESE, AppLanguage.fromTags("zh-CN"))
        assertEquals(AppLanguage.SIMPLIFIED_CHINESE, AppLanguage.fromTags("zh-CN-x-private"))
    }

    @Test
    fun unknownTagsFallBackToFollowSystem() {
        assertEquals(AppLanguage.FOLLOW_SYSTEM, AppLanguage.fromTags("fr"))
        assertEquals(AppLanguage.FOLLOW_SYSTEM, AppLanguage.fromTags("zh-TW"))
    }
}
