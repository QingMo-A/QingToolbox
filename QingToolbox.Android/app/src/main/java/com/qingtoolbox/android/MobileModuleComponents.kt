package com.qingtoolbox.android

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Check
import androidx.compose.material.icons.outlined.ChevronRight
import androidx.compose.material.icons.outlined.Clear
import androidx.compose.material.icons.outlined.Extension
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/** Renders a module's declared glyph on its declared accent, so packages stay icon-free. */
@Composable
fun ModuleGlyph(module: InstalledMobileModule, size: Dp = 44.dp) {
    val accent = remember(module.manifest.accent) { moduleAccentColor(module.manifest.accent) }
    QingIconSurface(
        modifier = Modifier.size(size),
        containerColor = accent.copy(alpha = 0.18f),
    ) {
        Text(
            text = module.manifest.glyph,
            color = accent,
            fontSize = (size.value * 0.42f).sp,
            fontWeight = FontWeight.SemiBold,
        )
    }
}

/**
 * Small chip describing the module's state.
 *
 * Only two states exist — in memory or on disk — and the label says which one, because the
 * modules page filters on exactly this distinction.
 */
@Composable
fun ModuleLoadChip(loaded: Boolean, modifier: Modifier = Modifier) {
    val label = stringResource(
        if (loaded) R.string.modules_state_loaded else R.string.modules_state_not_loaded,
    )
    val color = if (loaded) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant
    val shape = RoundedCornerShape(percent = 50)
    Text(
        text = label,
        style = MaterialTheme.typography.labelSmall,
        color = color,
        modifier = modifier
            .clip(shape)
            .border(LocalQingAppearance.current.borderWidth, color.copy(alpha = 0.48f), shape)
            .padding(horizontal = 8.dp, vertical = 2.dp),
    )
}

/** One module in the list: what it is, which version, and whether it is in memory. */
@Composable
fun ModuleListRow(
    module: InstalledMobileModule,
    loaded: Boolean,
    languageTag: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val name = module.displayName(languageTag)
    QingClickableCard(onClick = onClick, modifier = modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier.padding(16.dp),
            horizontalArrangement = Arrangement.spacedBy(14.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            ModuleGlyph(module = module)
            Column(modifier = Modifier.weight(1f)) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text(
                        text = name,
                        style = MaterialTheme.typography.titleMedium,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f, fill = false),
                    )
                    ModuleLoadChip(loaded = loaded)
                }
                Spacer(Modifier.height(2.dp))
                Text(
                    text = stringResource(R.string.modules_version_line, module.version),
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                val description = module.description(languageTag)
                if (description.isNotBlank()) {
                    Spacer(Modifier.height(4.dp))
                    Text(
                        text = description,
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
            Icon(
                imageVector = Icons.Outlined.ChevronRight,
                contentDescription = stringResource(R.string.open_item_content_description, name),
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

/** Compact home-grid tile. The name wraps to two lines so tiles stay the same height. */
@Composable
fun ModuleTile(
    module: InstalledMobileModule,
    loaded: Boolean,
    languageTag: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    QingClickableCard(onClick = onClick, modifier = modifier) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 14.dp, vertical = 14.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                ModuleGlyph(module = module, size = 38.dp)
                Spacer(Modifier.weight(1f))
                if (loaded) {
                    Icon(
                        imageVector = Icons.Outlined.Check,
                        contentDescription = stringResource(R.string.modules_state_loaded),
                        tint = MaterialTheme.colorScheme.primary,
                        modifier = Modifier.size(18.dp),
                    )
                }
            }
            Text(
                text = module.displayName(languageTag),
                style = MaterialTheme.typography.titleSmall,
                fontWeight = FontWeight.SemiBold,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

/** Search box over the installed modules. */
@Composable
fun ModuleSearchField(
    value: String,
    onValueChange: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        modifier = modifier.fillMaxWidth(),
        singleLine = true,
        placeholder = { Text(stringResource(R.string.modules_search_placeholder)) },
        leadingIcon = { Icon(Icons.Outlined.Search, contentDescription = null) },
        trailingIcon = {
            if (value.isNotEmpty()) {
                IconButton(onClick = { onValueChange("") }) {
                    Icon(
                        imageVector = Icons.Outlined.Clear,
                        contentDescription = stringResource(R.string.clear),
                    )
                }
            }
        },
        shape = RoundedCornerShape(LocalQingAppearance.current.controlCornerRadius),
    )
}

/**
 * Loading filter.
 *
 * It is a single narrow row that scrolls sideways, so the three choices never push the
 * module list off the screen on a small device.
 */
@Composable
fun ModuleFilterRow(
    selected: MobileModuleFilter,
    counts: Map<MobileModuleFilter, Int>,
    onSelect: (MobileModuleFilter) -> Unit,
    modifier: Modifier = Modifier,
) {
    LazyRow(
        modifier = modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        items(MobileModuleFilter.entries.size) { index ->
            val filter = MobileModuleFilter.entries[index]
            val count = counts[filter] ?: 0
            FilterChip(
                selected = filter == selected,
                onClick = { onSelect(filter) },
                label = {
                    Text(
                        text = if (count > 0) {
                            stringResource(filter.labelRes) + "  " + count
                        } else {
                            stringResource(filter.labelRes)
                        },
                    )
                },
            )
        }
    }
}

/** Capabilities the module declared, shown before the user decides to load it. */
@Composable
fun ModuleCapabilityRow(capabilities: List<String>) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        if (capabilities.isEmpty()) {
            Text(
                text = stringResource(R.string.modules_detail_no_capabilities),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            return@Column
        }
        capabilities.forEach { capability ->
            val label = MobileModuleCapabilities.labelRes(capability)
                ?.let { stringResource(it) }
                ?: capability
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Icon(
                    imageVector = Icons.Outlined.Check,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(16.dp),
                )
                Text(text = label, style = MaterialTheme.typography.bodyMedium)
            }
        }
    }
}

/**
 * Empty state for the module list.
 *
 * It only ever states the current situation and, when the shell can act on it, offers the
 * action that changes it — [ModulesScreen] passes the import action so the hint is
 * something the reader can use rather than a description of the shell's design.
 */
@Composable
fun ModuleEmptyCard(
    title: String,
    body: String,
    actionLabel: String? = null,
    onAction: (() -> Unit)? = null,
    modifier: Modifier = Modifier,
) {
    QingCard(modifier = modifier.fillMaxWidth()) {
        Column(
            modifier = Modifier.padding(20.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            QingIconSurface(
                modifier = Modifier.size(52.dp),
                containerColor = MaterialTheme.colorScheme.surfaceContainerHigh,
            ) {
                Icon(
                    imageVector = Icons.Outlined.Extension,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Text(
                text = title,
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.SemiBold,
            )
            Text(
                text = body,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            if (actionLabel != null && onAction != null) {
                QingPrimaryButton(onClick = onAction) { Text(actionLabel) }
            }
        }
    }
}

/** Section label used on the home page and the module detail page. */
@Composable
fun ModuleSectionTitle(title: String) {
    Text(
        text = title,
        style = MaterialTheme.typography.titleMedium,
        fontWeight = FontWeight.SemiBold,
        color = MaterialTheme.colorScheme.onSurface,
        modifier = Modifier.padding(top = 4.dp),
    )
}

private val DEFAULT_ACCENT_VALUE = MobileModuleManifest.DEFAULT_ACCENT
    .removePrefix("#")
    .toInt(16)

/**
 * Parses a manifest accent colour.
 *
 * Deliberately pure Kotlin rather than the platform colour parser: a manifest is untrusted
 * input, and an unusable value must degrade to the default accent instead of throwing.
 */
internal fun moduleAccentColor(accent: String): Color {
    val fallback = Color(0xFF000000.toInt() or DEFAULT_ACCENT_VALUE)
    val hex = accent.removePrefix("#")
    if (hex.length != 6) return fallback
    val value = hex.toLongOrNull(16) ?: return fallback
    return Color(0xFF000000.toInt() or value.toInt())
}

/** A key/value line inside the module detail page. */
@Composable
internal fun ModuleDetailRow(label: String, value: String) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 7.dp),
        horizontalArrangement = Arrangement.spacedBy(14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.weight(1f),
        )
        Text(
            text = value,
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.SemiBold,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

/** Divider variant that matches the shell's hairline styling. */
@Composable
internal fun ModuleHairline() {
    Spacer(
        modifier = Modifier
            .fillMaxWidth()
            .height(LocalQingAppearance.current.borderWidth)
            .background(LocalQingAppearance.current.cardBorderColor),
    )
}
