package com.qingtoolbox.android

import androidx.annotation.StringRes
import androidx.appcompat.app.AppCompatDelegate
import androidx.core.os.LocaleListCompat

enum class AppLanguage(
    val tag: String,
    @StringRes val labelRes: Int,
) {
    FOLLOW_SYSTEM("", R.string.language_follow_system),
    ENGLISH("en", R.string.language_english),
    SIMPLIFIED_CHINESE("zh-CN", R.string.language_simplified_chinese),
    ;

    companion object {
        fun fromTags(tags: String?): AppLanguage = when {
            tags.isNullOrBlank() -> FOLLOW_SYSTEM
            tags.equals("en", ignoreCase = true) || tags.startsWith("en-", ignoreCase = true) -> ENGLISH
            tags.equals("zh-CN", ignoreCase = true) || tags.startsWith("zh-CN-", ignoreCase = true) -> {
                SIMPLIFIED_CHINESE
            }
            else -> FOLLOW_SYSTEM
        }
    }
}

object AppLanguageManager {
    fun current(): AppLanguage = AppLanguage.fromTags(
        AppCompatDelegate.getApplicationLocales().toLanguageTags(),
    )

    fun apply(language: AppLanguage) {
        val locales = if (language == AppLanguage.FOLLOW_SYSTEM) {
            LocaleListCompat.getEmptyLocaleList()
        } else {
            LocaleListCompat.forLanguageTags(language.tag)
        }
        AppCompatDelegate.setApplicationLocales(locales)
    }
}
