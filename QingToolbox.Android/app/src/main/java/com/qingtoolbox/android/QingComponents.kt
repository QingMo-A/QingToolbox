package com.qingtoolbox.android

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CheckboxDefaults
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp

private val QingTransparent = Color.Transparent

private fun qingShape(radius: androidx.compose.ui.unit.Dp): Shape =
    RoundedCornerShape(radius)

@Composable
fun QingCard(
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    val style = LocalQingAppearance.current
    val shape = qingShape(style.cardCornerRadius)
    Card(
        modifier = modifier,
        shape = shape,
        border = BorderStroke(style.borderWidth, style.cardBorderColor),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.surfaceContainerLow,
        ),
        elevation = CardDefaults.cardElevation(defaultElevation = style.cardElevation),
        content = content,
    )
}

@Composable
fun QingClickableCard(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    selected: Boolean = false,
    content: @Composable ColumnScope.() -> Unit,
) {
    val style = LocalQingAppearance.current
    val shape = qingShape(style.cardCornerRadius)
    Card(
        onClick = onClick,
        modifier = modifier,
        enabled = enabled,
        shape = shape,
        border = BorderStroke(
            width = when {
                selected && enabled -> (style.borderWidth * 1.5f).coerceAtLeast(1.dp)
                else -> style.borderWidth
            },
            color = when {
                selected && enabled -> MaterialTheme.colorScheme.primary
                enabled -> style.cardBorderColor
                else -> MaterialTheme.colorScheme.outlineVariant
            },
        ),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.surfaceContainerLow,
            disabledContainerColor = MaterialTheme.colorScheme.surfaceContainerLow.copy(alpha = 0.60f),
        ),
        elevation = CardDefaults.cardElevation(defaultElevation = style.cardElevation),
        content = content,
    )
}

@Composable
fun QingIconSurface(
    modifier: Modifier = Modifier.size(48.dp),
    containerColor: Color = MaterialTheme.colorScheme.primaryContainer,
    usePrimaryBrush: Boolean = false,
    content: @Composable BoxScope.() -> Unit,
) {
    val style = LocalQingAppearance.current
    val shape = qingShape(style.controlCornerRadius)
    val brush = if (usePrimaryBrush) style.primaryBrush() else null
    val backgroundModifier = if (brush != null) {
        Modifier.background(brush, shape)
    } else {
        Modifier.background(containerColor, shape)
    }
    Box(
        modifier = modifier
            .shadow(style.controlElevation, shape)
            .clip(shape)
            .then(backgroundModifier)
            .border(style.borderWidth, style.controlBorderColor, shape),
        contentAlignment = Alignment.Center,
        content = content,
    )
}

@Composable
fun QingHeroCard(
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    val style = LocalQingAppearance.current
    val shape = qingShape(style.cardCornerRadius)
    val brush = style.primaryBrush()
    val backgroundModifier = if (brush != null) {
        Modifier.background(brush, shape)
    } else {
        Modifier.background(MaterialTheme.colorScheme.primaryContainer, shape)
    }
    val borderColor = if (style.glowStrength > 0f) {
        style.glowColor.copy(alpha = style.glowStrength * 0.30f)
    } else {
        style.cardBorderColor
    }
    Column(
        modifier = modifier
            .shadow(style.cardElevation, shape)
            .clip(shape)
            .then(backgroundModifier)
            .border(style.borderWidth, borderColor, shape)
            .padding(20.dp),
        content = content,
    )
}

@Composable
fun QingPrimaryButton(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    content: @Composable RowScope.() -> Unit,
) {
    val style = LocalQingAppearance.current
    val shape = qingShape(style.controlCornerRadius)
    val brush = style.primaryBrush()
    val backgroundModifier = if (brush != null && enabled) {
        Modifier.background(brush, shape)
    } else {
        Modifier
    }
    Button(
        onClick = onClick,
        modifier = modifier
            .defaultMinSize(minHeight = 48.dp)
            .then(backgroundModifier),
        enabled = enabled,
        shape = shape,
        border = BorderStroke(style.borderWidth, style.controlBorderColor),
        colors = if (brush != null && enabled) {
            ButtonDefaults.buttonColors(
                containerColor = QingTransparent,
                contentColor = MaterialTheme.colorScheme.onPrimary,
            )
        } else {
            ButtonDefaults.buttonColors()
        },
        elevation = ButtonDefaults.buttonElevation(
            defaultElevation = style.controlElevation,
            pressedElevation = style.controlElevation + style.pressedEmphasis.dp,
        ),
        content = content,
    )
}

@Composable
fun QingSecondaryButton(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    content: @Composable RowScope.() -> Unit,
) {
    val style = LocalQingAppearance.current
    val shape = qingShape(style.controlCornerRadius)
    OutlinedButton(
        onClick = onClick,
        modifier = modifier.defaultMinSize(minHeight = 48.dp),
        enabled = enabled,
        shape = shape,
        border = BorderStroke(style.borderWidth, style.controlBorderColor),
        colors = ButtonDefaults.outlinedButtonColors(),
        content = content,
    )
}

@Composable
fun QingListItem(
    modifier: Modifier = Modifier,
    leadingContent: @Composable (() -> Unit)? = null,
    headlineContent: @Composable () -> Unit,
    supportingContent: (@Composable (() -> Unit))? = null,
    trailingContent: (@Composable (() -> Unit))? = null,
) {
    val style = LocalQingAppearance.current
    val shape = qingShape(style.cardCornerRadius)
    ListItem(
        modifier = modifier
            .clip(shape)
            .border(style.borderWidth, style.cardBorderColor, shape),
        leadingContent = leadingContent,
        headlineContent = headlineContent,
        supportingContent = supportingContent,
        trailingContent = trailingContent,
        colors = ListItemDefaults.colors(
            containerColor = MaterialTheme.colorScheme.surfaceContainerLow,
        ),
    )
}

@Composable
fun QingSwitch(
    checked: Boolean,
    onCheckedChange: ((Boolean) -> Unit)?,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    val style = LocalQingAppearance.current
    val scheme = MaterialTheme.colorScheme
    val checkedBorder = style.controlBorderColor.copy(
        alpha = (0.55f + style.focusEmphasis).coerceAtMost(1f),
    )
    val uncheckedBorder = style.controlBorderColor.copy(
        alpha = (0.40f + style.hoverEmphasis).coerceAtMost(1f),
    )
    val colors = when (style.navigationStyle) {
        QingNavigationStyle.CIRCUIT -> SwitchDefaults.colors(
            checkedThumbColor = scheme.onPrimary,
            checkedTrackColor = scheme.primary,
            checkedBorderColor = checkedBorder,
            uncheckedThumbColor = scheme.primaryContainer,
            uncheckedTrackColor = scheme.surface,
            uncheckedBorderColor = uncheckedBorder,
        )
        QingNavigationStyle.TERMINAL -> SwitchDefaults.colors(
            checkedThumbColor = scheme.onPrimary,
            checkedTrackColor = scheme.primary,
            checkedBorderColor = checkedBorder,
            uncheckedThumbColor = scheme.surfaceContainerHigh,
            uncheckedTrackColor = scheme.surface,
            uncheckedBorderColor = uncheckedBorder,
        )
        QingNavigationStyle.SOFT -> SwitchDefaults.colors(
            checkedThumbColor = scheme.onPrimary,
            checkedTrackColor = scheme.primary,
            checkedBorderColor = checkedBorder,
            uncheckedThumbColor = scheme.onSurfaceVariant,
            uncheckedTrackColor = scheme.surfaceContainerHigh,
            uncheckedBorderColor = uncheckedBorder,
        )
        QingNavigationStyle.NOVA -> SwitchDefaults.colors(
            checkedThumbColor = scheme.onPrimary,
            checkedTrackColor = scheme.primary,
            checkedBorderColor = checkedBorder,
            uncheckedThumbColor = scheme.primaryContainer,
            uncheckedTrackColor = scheme.surface,
            uncheckedBorderColor = uncheckedBorder,
        )
        QingNavigationStyle.STANDARD -> SwitchDefaults.colors(
            checkedThumbColor = scheme.onPrimary,
            checkedTrackColor = scheme.primary,
            checkedBorderColor = checkedBorder,
            uncheckedThumbColor = scheme.outline,
            uncheckedTrackColor = scheme.surfaceContainerLow,
            uncheckedBorderColor = uncheckedBorder,
        )
    }
    Switch(
        checked = checked,
        onCheckedChange = onCheckedChange,
        modifier = modifier.defaultMinSize(minWidth = 48.dp, minHeight = 48.dp),
        enabled = enabled,
        colors = colors,
    )
}

@Composable
fun QingCheckbox(
    checked: Boolean,
    onCheckedChange: ((Boolean) -> Unit)?,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    val style = LocalQingAppearance.current
    val scheme = MaterialTheme.colorScheme
    Checkbox(
        checked = checked,
        onCheckedChange = onCheckedChange,
        modifier = modifier.defaultMinSize(minWidth = 48.dp, minHeight = 48.dp),
        enabled = enabled,
        colors = CheckboxDefaults.colors(
            checkedColor = scheme.primary,
            checkmarkColor = scheme.onPrimary,
            uncheckedColor = style.controlBorderColor,
            disabledCheckedColor = scheme.primary.copy(alpha = 0.38f),
            disabledUncheckedColor = scheme.outline.copy(alpha = 0.38f),
        ),
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun QingTopAppBar(
    title: @Composable () -> Unit,
    modifier: Modifier = Modifier,
) {
    val style = LocalQingAppearance.current
    val shape = RoundedCornerShape(
        bottomStart = style.cardCornerRadius,
        bottomEnd = style.cardCornerRadius,
    )
    val containerColor = when (style.navigationStyle) {
        QingNavigationStyle.CIRCUIT -> MaterialTheme.colorScheme.surface.copy(alpha = 0.96f)
        QingNavigationStyle.TERMINAL -> MaterialTheme.colorScheme.surface
        QingNavigationStyle.SOFT -> MaterialTheme.colorScheme.surfaceContainer.copy(alpha = 0.94f)
        QingNavigationStyle.NOVA -> MaterialTheme.colorScheme.surface.copy(alpha = 0.96f)
        QingNavigationStyle.STANDARD -> MaterialTheme.colorScheme.background
    }
    TopAppBar(
        modifier = modifier
            .clip(shape)
            .border(style.borderWidth, style.cardBorderColor, shape),
        title = title,
        colors = TopAppBarDefaults.topAppBarColors(
            containerColor = containerColor,
            scrolledContainerColor = containerColor,
        ),
    )
}

@Composable
fun QingDivider(modifier: Modifier = Modifier) {
    val style = LocalQingAppearance.current
    HorizontalDivider(
        modifier = modifier,
        thickness = style.borderWidth,
        color = style.cardBorderColor,
    )
}

@Composable
fun QingNavigationBar(
    modifier: Modifier = Modifier,
    content: @Composable RowScope.() -> Unit,
) {
    val style = LocalQingAppearance.current
    val shape = RoundedCornerShape(topStart = style.cardCornerRadius, topEnd = style.cardCornerRadius)
    val containerColor = when (style.navigationStyle) {
        QingNavigationStyle.CIRCUIT -> MaterialTheme.colorScheme.surface.copy(alpha = 0.96f)
        QingNavigationStyle.TERMINAL -> MaterialTheme.colorScheme.surface
        QingNavigationStyle.SOFT -> MaterialTheme.colorScheme.surfaceContainer
        QingNavigationStyle.NOVA -> MaterialTheme.colorScheme.surface.copy(alpha = 0.98f)
        QingNavigationStyle.STANDARD -> MaterialTheme.colorScheme.surface
    }
    NavigationBar(
        modifier = modifier
            .clip(shape)
            .border(style.borderWidth, style.cardBorderColor, shape),
        containerColor = containerColor,
        tonalElevation = style.cardElevation,
        content = content,
    )
}

@Composable
fun RowScope.QingNavigationBarItem(
    selected: Boolean,
    onClick: () -> Unit,
    icon: @Composable () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    label: (@Composable () -> Unit)? = null,
) {
    val style = LocalQingAppearance.current
    val primary = MaterialTheme.colorScheme.primary
    val itemColors = when (style.navigationStyle) {
        QingNavigationStyle.CIRCUIT -> NavigationBarItemDefaults.colors(
            selectedIconColor = primary,
            selectedTextColor = primary,
            indicatorColor = primary.copy(alpha = 0.18f),
        )
        QingNavigationStyle.TERMINAL -> NavigationBarItemDefaults.colors(
            selectedIconColor = primary,
            selectedTextColor = primary,
            indicatorColor = primary.copy(alpha = 0.12f),
        )
        QingNavigationStyle.SOFT -> NavigationBarItemDefaults.colors(
            selectedIconColor = MaterialTheme.colorScheme.onPrimaryContainer,
            selectedTextColor = MaterialTheme.colorScheme.onSurface,
            indicatorColor = MaterialTheme.colorScheme.primaryContainer.copy(alpha = 0.78f),
        )
        QingNavigationStyle.NOVA -> NavigationBarItemDefaults.colors(
            selectedIconColor = primary,
            selectedTextColor = primary,
            indicatorColor = primary.copy(alpha = 0.17f),
        )
        QingNavigationStyle.STANDARD -> NavigationBarItemDefaults.colors()
    }
    NavigationBarItem(
        selected = selected,
        onClick = onClick,
        icon = icon,
        modifier = modifier,
        enabled = enabled,
        label = label,
        colors = itemColors,
    )
}

@Composable
fun QingEmptyState(
    icon: @Composable () -> Unit,
    title: String,
    body: String,
    modifier: Modifier = Modifier,
) {
    Box(modifier = modifier, contentAlignment = Alignment.Center) {
        QingCard(
            modifier = Modifier
                .fillMaxWidth()
                .padding(24.dp),
        ) {
            Column(
                modifier = Modifier.padding(28.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                QingIconSurface(
                    modifier = Modifier.size(56.dp),
                    containerColor = MaterialTheme.colorScheme.primaryContainer,
                ) {
                    icon()
                }
                Text(
                    title,
                    style = MaterialTheme.typography.titleLarge,
                    textAlign = TextAlign.Center,
                )
                Text(
                    body,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    textAlign = TextAlign.Center,
                )
            }
        }
    }
}

@Composable
fun QingThemePreview(
    theme: AppearanceTheme,
    modifier: Modifier = Modifier,
) {
    QingToolboxTheme(appearance = theme) {
        val style = LocalQingAppearance.current
        val shape = qingShape(style.controlCornerRadius)
        val primaryBrush = style.primaryBrush()
        Row(
            modifier = modifier
                .fillMaxWidth()
                .clip(qingShape(style.cardCornerRadius))
                .background(MaterialTheme.colorScheme.surface)
                .border(style.borderWidth, style.cardBorderColor, qingShape(style.cardCornerRadius))
                .padding(horizontal = 12.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            PreviewSwatch(
                label = "Primary",
                shape = shape,
                brush = primaryBrush,
                color = MaterialTheme.colorScheme.primary,
                contentColor = MaterialTheme.colorScheme.onPrimary,
                modifier = Modifier.weight(1f),
            )
            PreviewSwatch(
                label = "Secondary",
                shape = shape,
                brush = null,
                color = MaterialTheme.colorScheme.secondaryContainer,
                contentColor = MaterialTheme.colorScheme.onSecondaryContainer,
                modifier = Modifier.weight(1f),
                borderColor = style.controlBorderColor,
            )
            QingSwitch(
                checked = true,
                onCheckedChange = {},
                enabled = true,
            )
        }
    }
}

@Composable
private fun PreviewSwatch(
    label: String,
    shape: Shape,
    brush: Brush?,
    color: Color,
    contentColor: Color,
    modifier: Modifier = Modifier,
    borderColor: Color = Color.Transparent,
) {
    val backgroundModifier = if (brush != null) {
        Modifier.background(brush, shape)
    } else {
        Modifier.background(color, shape)
    }
    Box(
        modifier = modifier
            .defaultMinSize(minHeight = 40.dp)
            .clip(shape)
            .then(backgroundModifier)
            .border(1.dp, borderColor, shape),
        contentAlignment = Alignment.Center,
    ) {
        Text(label, color = contentColor, style = MaterialTheme.typography.labelMedium)
    }
}

@Composable
fun QingStatusText(
    text: String,
    modifier: Modifier = Modifier,
) {
    val style = LocalQingAppearance.current
    Text(
        text = text,
        modifier = modifier,
        style = MaterialTheme.typography.bodySmall,
        fontFamily = if (style.technicalMonospaceEnabled) FontFamily.Monospace else null,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
}
