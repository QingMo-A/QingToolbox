package com.qingtoolbox.android

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
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
import androidx.compose.material.icons.outlined.DevicesOther
import androidx.compose.material.icons.outlined.Home
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.outlined.Language
import androidx.compose.material.icons.outlined.Palette
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material.icons.outlined.Tune
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
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

@OptIn(ExperimentalMaterial3Api::class)
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

    BackHandler(enabled = navController.previousBackStackEntry != null) {
        navController.popBackStack()
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text(
                            text = currentDestination.label,
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
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.background,
                ),
            )
        },
        bottomBar = {
            NavigationBar {
                destinations.forEach { destination ->
                    NavigationBarItem(
                        selected = currentDestination.route == destination.route,
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
            composable(MobileDestination.Home.route) { HomeScreen() }
            composable(MobileDestination.Tools.route) { ToolsScreen() }
            composable(MobileDestination.Devices.route) { DevicesScreen() }
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
private fun HomeScreen(modifier: Modifier = Modifier) {
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        item {
            Surface(
                modifier = Modifier.fillMaxWidth(),
                color = MaterialTheme.colorScheme.primaryContainer,
                shape = MaterialTheme.shapes.extraLarge,
            ) {
                Row(
                    modifier = Modifier.padding(20.dp),
                    horizontalArrangement = Arrangement.spacedBy(16.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Surface(
                        modifier = Modifier.size(56.dp),
                        shape = MaterialTheme.shapes.large,
                        color = MaterialTheme.colorScheme.primary,
                    ) {
                        Box(contentAlignment = Alignment.Center) {
                            Icon(
                                imageVector = Icons.Outlined.AutoAwesome,
                                contentDescription = null,
                                tint = MaterialTheme.colorScheme.onPrimary,
                            )
                        }
                    }
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = "Welcome to QingToolbox",
                            style = MaterialTheme.typography.headlineSmall,
                            fontWeight = FontWeight.SemiBold,
                            color = MaterialTheme.colorScheme.onPrimaryContainer,
                        )
                        Spacer(Modifier.height(4.dp))
                        Text(
                            text = "A native Android shell for your everyday toolbox.",
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onPrimaryContainer,
                        )
                    }
                }
            }
        }
        item { SectionHeader(title = "Frequently used") }
        item {
            PlaceholderCard(
                icon = Icons.Outlined.Tune,
                title = "Your frequent tools will appear here",
                body = "Built-in mobile tools are planned for the next milestone.",
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
private fun ToolsScreen(modifier: Modifier = Modifier) {
    EmptyStateScreen(
        modifier = modifier,
        icon = Icons.Outlined.Build,
        title = "No tools yet",
        body = "QingToolbox mobile tools will appear here as they become available.",
    )
}

@Composable
private fun DevicesScreen(modifier: Modifier = Modifier) {
    EmptyStateScreen(
        modifier = modifier,
        icon = Icons.Outlined.DevicesOther,
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
            ListItem(
                leadingContent = {
                    Icon(Icons.Outlined.Language, contentDescription = null)
                },
                headlineContent = { Text("Language") },
                supportingContent = { Text("English (default) · More languages are planned") },
            )
        }
        item { HorizontalDivider() }
        item { SectionHeader(title = "About") }
        item {
            ListItem(
                leadingContent = { Icon(Icons.Outlined.Info, contentDescription = null) },
                headlineContent = { Text("QingToolbox Android") },
                supportingContent = { Text("A first-class mobile shell built with Jetpack Compose") },
            )
        }
        item {
            ListItem(
                leadingContent = { Icon(Icons.Outlined.Check, contentDescription = null) },
                headlineContent = { Text("Version") },
                supportingContent = { Text("${BuildConfig.VERSION_NAME} · M0 shell") },
            )
        }
        item {
            Text(
                text = "Root capabilities, cross-device transfer, and mobile modules are intentionally not part of M0.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
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
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick),
        border = if (selected) {
            BorderStroke(2.dp, MaterialTheme.colorScheme.primary)
        } else {
            null
        },
        colors = CardDefaults.cardColors(
            containerColor = if (selected) {
                MaterialTheme.colorScheme.primaryContainer
            } else {
                MaterialTheme.colorScheme.surfaceContainerLow
            },
        ),
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
    }
}

@Composable
private fun ThemeSwatch(theme: AppearanceTheme, selected: Boolean) {
    val color = when (theme) {
        AppearanceTheme.QING_DEFAULT -> androidx.compose.ui.graphics.Color(0xFF006C4C)
        AppearanceTheme.NEON_CIRCUIT -> androidx.compose.ui.graphics.Color(0xFF006874)
        AppearanceTheme.GREENLINE -> androidx.compose.ui.graphics.Color(0xFF426500)
        AppearanceTheme.AURORA_FLOW -> androidx.compose.ui.graphics.Color(0xFF465D91)
        AppearanceTheme.QING_NOVA -> androidx.compose.ui.graphics.Color(0xFF7A4A7E)
    }
    Surface(
        modifier = Modifier.size(if (selected) 44.dp else 40.dp),
        shape = MaterialTheme.shapes.large,
        color = color,
    ) {}
}

@Composable
private fun EmptyStateScreen(
    icon: ImageVector,
    title: String,
    body: String,
    modifier: Modifier = Modifier,
) {
    Box(
        modifier = modifier
            .fillMaxSize()
            .padding(24.dp),
        contentAlignment = Alignment.Center,
    ) {
        Card(
            modifier = Modifier.fillMaxWidth(),
            colors = CardDefaults.cardColors(
                containerColor = MaterialTheme.colorScheme.surfaceContainerLow,
            ),
        ) {
            Column(
                modifier = Modifier.padding(28.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                Icon(
                    imageVector = icon,
                    contentDescription = null,
                    modifier = Modifier.size(40.dp),
                    tint = MaterialTheme.colorScheme.primary,
                )
                Text(
                    text = title,
                    style = MaterialTheme.typography.titleLarge,
                    fontWeight = FontWeight.SemiBold,
                    textAlign = TextAlign.Center,
                )
                Text(
                    text = body,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    textAlign = TextAlign.Center,
                )
            }
        }
    }
}

@Composable
private fun PlaceholderCard(icon: ImageVector, title: String, body: String) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.surfaceContainerLow,
        ),
    ) {
        Row(
            modifier = Modifier.padding(18.dp),
            horizontalArrangement = Arrangement.spacedBy(14.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                imageVector = icon,
                contentDescription = null,
                modifier = Modifier.size(28.dp),
                tint = MaterialTheme.colorScheme.primary,
            )
            Column {
                Text(title, style = MaterialTheme.typography.titleMedium)
                Spacer(Modifier.height(4.dp))
                Text(
                    body,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

@Composable
private fun SettingsSummaryCard(icon: ImageVector, title: String, body: String) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.surfaceContainerLow,
        ),
    ) {
        ListItem(
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
