package com.qingtoolbox.android

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.AutoAwesome
import androidx.compose.material.icons.outlined.Build
import androidx.compose.material.icons.outlined.Check
import androidx.compose.material.icons.outlined.Calculate
import androidx.compose.material.icons.outlined.ChevronRight
import androidx.compose.material.icons.outlined.DevicesOther
import androidx.compose.material.icons.outlined.Home
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.outlined.Language
import androidx.compose.material.icons.outlined.Palette
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavHostController
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController

private sealed class MobileDestination(
    val route: String,
    val label: String,
    val icon: ImageVector,
) {
    data object Home : MobileDestination("home", "Home", Icons.Outlined.Home)
    data object Tools : MobileDestination("tools", "Tools", Icons.Outlined.Build)
    data object Devices : MobileDestination("devices", "Devices", Icons.Outlined.DevicesOther)
    data object Settings : MobileDestination("settings", "Settings", Icons.Outlined.Settings)
    data object FileHash : MobileDestination("tools/file-hash", "File Hash", Icons.Outlined.Calculate)
}

private val destinations = listOf(
    MobileDestination.Home,
    MobileDestination.Tools,
    MobileDestination.Devices,
    MobileDestination.Settings,
)

@Composable
fun QingToolboxApp(viewModel: QingToolboxViewModel) {
    val uiState by viewModel.uiState.collectAsStateWithLifecycle()

    QingToolboxTheme(appearance = uiState.appearance) {
        QingToolboxShell(
            currentAppearance = uiState.appearance,
            onAppearanceSelected = viewModel::selectAppearance,
        )
    }
}

@Composable
private fun QingToolboxShell(
    currentAppearance: AppearanceTheme,
    onAppearanceSelected: (AppearanceTheme) -> Unit,
) {
    val navController = rememberNavController()
    val backStackEntry by navController.currentBackStackEntryAsState()
    val currentRoute = backStackEntry?.destination?.route
    val currentDestination = destinations.firstOrNull { it.route == currentRoute }
        ?: MobileDestination.Home
    val selectedDestination = if (currentRoute == MobileDestination.FileHash.route) {
        MobileDestination.Tools
    } else {
        currentDestination
    }
    val titleDestination = if (currentRoute == MobileDestination.FileHash.route) {
        MobileDestination.FileHash
    } else {
        currentDestination
    }

    BackHandler(enabled = navController.previousBackStackEntry != null) {
        if (currentRoute == MobileDestination.FileHash.route) {
            navigateTo(navController, MobileDestination.Tools)
        } else {
            navController.popBackStack()
        }
    }

    Scaffold(
        topBar = {
            QingTopAppBar(
                title = {
                    Column {
                        Text(
                            text = titleDestination.label,
                            style = MaterialTheme.typography.titleLarge,
                            fontWeight = FontWeight.SemiBold,
                        )
                        Text(
                            text = "QingToolbox mobile",
                            style = MaterialTheme.typography.labelMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                },
            )
        },
        bottomBar = {
            QingNavigationBar {
                destinations.forEach { destination ->
                    QingNavigationBarItem(
                        selected = selectedDestination.route == destination.route,
                        onClick = { navigateTo(navController, destination) },
                        icon = { Icon(destination.icon, contentDescription = null) },
                        label = { Text(destination.label) },
                    )
                }
            }
        },
        containerColor = MaterialTheme.colorScheme.background,
    ) { innerPadding ->
        NavHost(
            navController = navController,
            startDestination = MobileDestination.Home.route,
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding),
        ) {
            composable(MobileDestination.Home.route) {
                HomeScreen(onFileHashClick = { navigateTo(navController, MobileDestination.FileHash) })
            }
            composable(MobileDestination.Tools.route) {
                ToolsScreen(onFileHashClick = { navigateTo(navController, MobileDestination.FileHash) })
            }
            composable(MobileDestination.Devices.route) { DevicesScreen() }
            composable(MobileDestination.FileHash.route) { FileHashScreen() }
            composable(MobileDestination.Settings.route) {
                SettingsScreen(
                    currentAppearance = currentAppearance,
                    onAppearanceSelected = onAppearanceSelected,
                )
            }
        }
    }
}

private fun navigateTo(navController: NavHostController, destination: MobileDestination) {
    navController.navigate(destination.route) {
        popUpTo(navController.graph.startDestinationId) {
            saveState = true
        }
        launchSingleTop = true
        restoreState = true
    }
}

@Composable
private fun HomeScreen(
    onFileHashClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val heroContentColor = if (LocalQingAppearance.current.primaryBrush() != null) {
        MaterialTheme.colorScheme.onPrimary
    } else {
        MaterialTheme.colorScheme.onPrimaryContainer
    }
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        item {
            QingHeroCard(
                modifier = Modifier.fillMaxWidth(),
            ) {
                Row(
                    horizontalArrangement = Arrangement.spacedBy(16.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    QingIconSurface(
                        modifier = Modifier.size(56.dp),
                        usePrimaryBrush = true,
                        containerColor = MaterialTheme.colorScheme.primary,
                    ) {
                        Icon(
                            imageVector = Icons.Outlined.AutoAwesome,
                            contentDescription = null,
                            tint = MaterialTheme.colorScheme.onPrimary,
                        )
                    }
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = "Welcome to QingToolbox",
                            style = MaterialTheme.typography.headlineSmall,
                            fontWeight = FontWeight.SemiBold,
                            color = heroContentColor,
                        )
                        Spacer(Modifier.height(4.dp))
                        Text(
                            text = "A native Android shell for your everyday toolbox.",
                            style = MaterialTheme.typography.bodyMedium,
                            color = heroContentColor,
                        )
                    }
                }
            }
        }
        item { SectionHeader(title = "Frequently used") }
        item {
            PlaceholderCard(
                icon = Icons.Outlined.Calculate,
                title = "File Hash",
                body = "Calculate common hashes for a local file.",
                onClick = onFileHashClick,
            )
        }
        item { SectionHeader(title = "Recently used") }
        item {
            PlaceholderCard(
                icon = Icons.Outlined.Build,
                title = "No recent activity",
                body = "When you use a tool, its latest activity will be easy to find here.",
            )
        }
    }
}

@Composable
private fun ToolsScreen(
    onFileHashClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item { SectionHeader(title = "Tools") }
        item {
            PlaceholderCard(
                icon = Icons.Outlined.Calculate,
                title = "File Hash",
                body = "Calculate common hashes for a local file.",
                onClick = onFileHashClick,
            )
        }
    }
}

@Composable
private fun DevicesScreen(modifier: Modifier = Modifier) {
    QingEmptyState(
        modifier = modifier,
        icon = {
            Icon(
                imageVector = Icons.Outlined.DevicesOther,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.primary,
            )
        },
        title = "No devices connected",
        body = "Cross-device connections are planned for a future milestone.",
    )
}

@Composable
private fun SettingsScreen(
    currentAppearance: AppearanceTheme,
    onAppearanceSelected: (AppearanceTheme) -> Unit,
    modifier: Modifier = Modifier,
) {
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item { SectionHeader(title = "Appearance") }
        item {
            SettingsSummaryCard(
                icon = Icons.Outlined.Palette,
                title = "Theme",
                body = currentAppearance.label,
            )
        }
        item {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                AppearanceTheme.entries.forEach { theme ->
                    AppearanceOption(
                        theme = theme,
                        selected = theme == currentAppearance,
                        onClick = { onAppearanceSelected(theme) },
                    )
                }
            }
        }
        item { SectionHeader(title = "Language") }
        item {
            QingListItem(
                leadingContent = {
                    Icon(Icons.Outlined.Language, contentDescription = null)
                },
                headlineContent = { Text("Language") },
                supportingContent = { Text("English (default) · More languages are planned") },
            )
        }
        item { QingDivider() }
        item { SectionHeader(title = "About") }
        item {
            QingListItem(
                leadingContent = { Icon(Icons.Outlined.Info, contentDescription = null) },
                headlineContent = { Text("QingToolbox Android") },
                supportingContent = { Text("A first-class mobile shell built with Jetpack Compose") },
            )
        }
        item {
            QingListItem(
                leadingContent = { Icon(Icons.Outlined.Check, contentDescription = null) },
                headlineContent = { Text("Version") },
                supportingContent = { Text("${BuildConfig.VERSION_NAME} · M0 shell") },
            )
        }
        item {
            QingStatusText(
                text = "Root capabilities, cross-device transfer, and mobile modules are intentionally not part of M0.",
                modifier = Modifier.padding(top = 4.dp, bottom = 20.dp),
            )
        }
    }
}

@Composable
private fun AppearanceOption(
    theme: AppearanceTheme,
    selected: Boolean,
    onClick: () -> Unit,
) {
    QingClickableCard(
        onClick = onClick,
        modifier = Modifier.fillMaxWidth(),
        selected = selected,
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 16.dp, vertical = 14.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            ThemeSwatch(theme = theme, selected = selected)
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = theme.label,
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
                )
                Text(
                    text = theme.description,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            if (selected) {
                Icon(
                    imageVector = Icons.Outlined.Check,
                    contentDescription = "Selected",
                    tint = MaterialTheme.colorScheme.primary,
                )
            }
        }
        QingThemePreview(
            theme = theme,
            modifier = Modifier.padding(horizontal = 16.dp, vertical = 0.dp),
        )
    }
}

@Composable
private fun ThemeSwatch(theme: AppearanceTheme, selected: Boolean) {
    val color = when (theme) {
        AppearanceTheme.QING_DEFAULT -> androidx.compose.ui.graphics.Color(0xFF006C4C)
        AppearanceTheme.NEON_CIRCUIT -> androidx.compose.ui.graphics.Color(0xFF006874)
        AppearanceTheme.GREENLINE -> androidx.compose.ui.graphics.Color(0xFF426500)
        AppearanceTheme.AURORA_FLOW -> androidx.compose.ui.graphics.Color(0xFF465D91)
        AppearanceTheme.QING_NOVA -> androidx.compose.ui.graphics.Color(0xFF4C9AFF)
    }
    QingIconSurface(
        modifier = Modifier.size(if (selected) 44.dp else 40.dp),
        containerColor = color,
    ) {}
}

@Composable
private fun PlaceholderCard(
    icon: ImageVector,
    title: String,
    body: String,
    onClick: (() -> Unit)? = null,
) {
    val content: @Composable ColumnScope.() -> Unit = {
        Row(
            modifier = Modifier.padding(18.dp),
            horizontalArrangement = Arrangement.spacedBy(14.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            QingIconSurface(modifier = Modifier.size(44.dp)) {
                Icon(
                    imageVector = icon,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                )
            }
            Column(modifier = Modifier.weight(1f)) {
                Text(title, style = MaterialTheme.typography.titleMedium)
                Spacer(Modifier.height(4.dp))
                Text(
                    body,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            if (onClick != null) {
                Icon(
                    imageVector = Icons.Outlined.ChevronRight,
                    contentDescription = "Open $title",
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
    if (onClick != null) {
        QingClickableCard(onClick = onClick, modifier = Modifier.fillMaxWidth(), content = content)
    } else {
        QingCard(modifier = Modifier.fillMaxWidth(), content = content)
    }
}

@Composable
private fun SettingsSummaryCard(icon: ImageVector, title: String, body: String) {
    QingCard(modifier = Modifier.fillMaxWidth()) {
        QingListItem(
            leadingContent = { Icon(icon, contentDescription = null) },
            headlineContent = { Text(title) },
            supportingContent = { Text(body) },
        )
    }
}

@Composable
private fun SectionHeader(title: String) {
    Text(
        text = title,
        style = MaterialTheme.typography.titleMedium,
        fontWeight = FontWeight.SemiBold,
        color = MaterialTheme.colorScheme.onSurface,
        modifier = Modifier.padding(top = 4.dp),
    )
}
