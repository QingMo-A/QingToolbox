package com.qingtoolbox.android

import androidx.annotation.StringRes

/** Filters the module list can be narrowed to. */
enum class MobileModuleFilter(@StringRes val labelRes: Int) {
    ALL(R.string.modules_filter_all),
    LOADED(R.string.modules_filter_loaded),
    NOT_LOADED(R.string.modules_filter_not_loaded),
}

/**
 * Search and filter for the installed module list.
 *
 * Kept free of Android types and of the runtime so the behaviour the user sees when
 * typing or switching a filter can be verified without a device.
 */
object MobileModuleQuery {
    fun apply(
        modules: List<InstalledMobileModule>,
        query: String,
        filter: MobileModuleFilter,
        loadedIds: Set<String>,
        languageTag: String,
    ): List<InstalledMobileModule> {
        val trimmed = query.trim()
        return modules
            .asSequence()
            .filter { module ->
                when (filter) {
                    MobileModuleFilter.ALL -> true
                    MobileModuleFilter.LOADED -> module.id in loadedIds
                    MobileModuleFilter.NOT_LOADED -> module.id !in loadedIds
                }
            }
            .filter { module ->
                trimmed.isEmpty() || module.matches(trimmed, languageTag)
            }
            .sortedWith(
                compareBy(
                    { it.displayName(languageTag).lowercase() },
                    { it.id },
                ),
            )
            .toList()
    }

    private fun InstalledMobileModule.matches(query: String, languageTag: String): Boolean {
        val needle = query.lowercase()
        return id.lowercase().contains(needle) ||
            version.lowercase().contains(needle) ||
            displayName(languageTag).lowercase().contains(needle) ||
            description(languageTag).lowercase().contains(needle)
    }
}
