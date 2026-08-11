package com.qingtoolbox.android

import androidx.annotation.StringRes
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
import androidx.compose.material.icons.outlined.Code
import androidx.compose.material.icons.outlined.DevicesOther
import androidx.compose.material.icons.outlined.Home
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.outlined.Language
import androidx.compose.material.icons.outlined.Palette
import androidx.compose.material.icons.outlined.QrCode2
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.AlertDialog
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource
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
    @StringRes val labelRes: Int,
    val icon: ImageVector,
) {
    data object Home : MobileDestination("home", R.string.nav_home, Icons.Outlined.Home)
    data object Tools : MobileDestination("tools", R.string.nav_tools, Icons.Outlined.Build)
    data object Devices : MobileDestination("devices", R.string.nav_devices, Icons.Outlined.DevicesOther)
    data object Settings : MobileDestination("settings", R.string.nav_settings, Icons.Outlined.Settings)
    data object FileHash : MobileDestination("tools/file-hash", R.string.destination_file_hash, Icons.Outlined.Calculate)
    data object TextCodec : MobileDestination("tools/text-codec", R.string.destination_text_codec, Icons.Outlined.Code)
    data object DeviceInfo : MobileDestination("tools/device-info", R.string.destination_device_info, Icons.Outlined.DevicesOther)
    data object QrCode : MobileDestination("tools/qr-code", R.string.destination_qr_code, Icons.Outlined.QrCode2)
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
    val isToolDetail = currentRoute == MobileDestination.FileHash.route ||
        currentRoute == MobileDestination.TextCodec.route ||
        currentRoute == MobileDestination.DeviceInfo.route ||
        currentRoute == MobileDestination.QrCode.route
    val selectedDestination = if (isToolDetail) {
        MobileDestination.Tools
    } else {
        currentDestination
    }
    val titleDestination = when (currentRoute) {
        MobileDestination.FileHash.route -> MobileDestination.FileHash
        MobileDestination.TextCodec.route -> MobileDestination.TextCodec
        MobileDestination.DeviceInfo.route -> MobileDestination.DeviceInfo
        MobileDestination.QrCode.route -> MobileDestination.QrCode
        else -> currentDestination
    }

    BackHandler(enabled = navController.previousBackStackEntry != null) {
        if (isToolDetail) {
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
                            text = stringResource(titleDestination.labelRes),
                            style = MaterialTheme.typography.titleLarge,
                            fontWeight = FontWeight.SemiBold,
                        )
                        Text(
                            text = stringResource(R.string.shell_subtitle),
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
                        label = { Text(stringResource(destination.labelRes)) },
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
                HomeScreen(
                    onFileHashClick = { navigateTo(navController, MobileDestination.FileHash) },
                    onTextCodecClick = { navigateTo(navController, MobileDestination.TextCodec) },
                    onDeviceInfoClick = { navigateTo(navController, MobileDestination.DeviceInfo) },
                    onQrCodeClick = { navigateTo(navController, MobileDestination.QrCode) },
                )
            }
            composable(MobileDestination.Tools.route) {
                ToolsScreen(
                    onFileHashClick = { navigateTo(navController, MobileDestination.FileHash) },
                    onTextCodecClick = { navigateTo(navController, MobileDestination.TextCodec) },
                    onDeviceInfoClick = { navigateTo(navController, MobileDestination.DeviceInfo) },
                    onQrCodeClick = { navigateTo(navController, MobileDestination.QrCode) },
                )
            }
            composable(MobileDestination.Devices.route) { QingTransferDevicesScreen() }
            composable(MobileDestination.FileHash.route) { FileHashScreen() }
            composable(MobileDestination.TextCodec.route) { TextCodecScreen() }
            composable(MobileDestination.DeviceInfo.route) { DeviceInfoScreen() }
            composable(MobileDestination.QrCode.route) { QrCodeScreen() }
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
    onTextCodecClick: () -> Unit,
    onDeviceInfoClick: () -> Unit,
    onQrCodeClick: () -> Unit,
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
                            text = stringResource(R.string.home_welcome_title),
                            style = MaterialTheme.typography.headlineSmall,
                            fontWeight = FontWeight.SemiBold,
                            color = heroContentColor,
                        )
                        Spacer(Modifier.height(4.dp))
                        Text(
                            text = stringResource(R.string.home_welcome_body),
                            style = MaterialTheme.typography.bodyMedium,
                            color = heroContentColor,
                        )
                    }
                }
            }
        }
        item { SectionHeader(title = stringResource(R.string.home_quick_tools)) }
        item {
            PlaceholderCard(
                icon = Icons.Outlined.Calculate,
                title = stringResource(R.string.destination_file_hash),
                body = stringResource(R.string.tool_file_hash_description),
                onClick = onFileHashClick,
            )
        }
        item {
            PlaceholderCard(
                icon = Icons.Outlined.Code,
                title = stringResource(R.string.destination_text_codec),
                body = stringResource(R.string.tool_text_codec_description),
                onClick = onTextCodecClick,
            )
        }
        item {
            PlaceholderCard(
                icon = Icons.Outlined.DevicesOther,
                title = stringResource(R.string.destination_device_info),
                body = stringResource(R.string.tool_device_info_description),
                onClick = onDeviceInfoClick,
            )
        }
        item {
            PlaceholderCard(
                icon = Icons.Outlined.QrCode2,
                title = stringResource(R.string.destination_qr_code),
                body = stringResource(R.string.tool_qr_code_description),
                onClick = onQrCodeClick,
            )
        }
        item { SectionHeader(title = stringResource(R.string.home_recently_used)) }
        item {
            PlaceholderCard(
                icon = Icons.Outlined.Build,
                title = stringResource(R.string.home_no_recent_activity),
                body = stringResource(R.string.home_recent_body),
            )
        }
    }
}

@Composable
private fun ToolsScreen(
    onFileHashClick: () -> Unit,
    onTextCodecClick: () -> Unit,
    onDeviceInfoClick: () -> Unit,
    onQrCodeClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item { SectionHeader(title = stringResource(R.string.nav_tools)) }
        item {
            PlaceholderCard(
                icon = Icons.Outlined.Calculate,
                title = stringResource(R.string.destination_file_hash),
                body = stringResource(R.string.tool_file_hash_description),
                onClick = onFileHashClick,
            )
        }
        item {
            PlaceholderCard(
                icon = Icons.Outlined.Code,
                title = stringResource(R.string.destination_text_codec),
                body = stringResource(R.string.tool_text_codec_description),
                onClick = onTextCodecClick,
            )
        }
        item {
            PlaceholderCard(
                icon = Icons.Outlined.DevicesOther,
                title = stringResource(R.string.destination_device_info),
                body = stringResource(R.string.tool_device_info_description),
                onClick = onDeviceInfoClick,
            )
        }
        item {
            PlaceholderCard(
                icon = Icons.Outlined.QrCode2,
                title = stringResource(R.string.destination_qr_code),
                body = stringResource(R.string.tool_qr_code_description),
                onClick = onQrCodeClick,
            )
        }
    }
}

@Composable
private fun SettingsScreen(
    currentAppearance: AppearanceTheme,
    onAppearanceSelected: (AppearanceTheme) -> Unit,
    modifier: Modifier = Modifier,
) {
    var showLanguageDialog by remember { mutableStateOf(false) }
    val currentLanguage = AppLanguageManager.current()
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item { SectionHeader(title = stringResource(R.string.settings_appearance)) }
        item {
            SettingsSummaryCard(
                icon = Icons.Outlined.Palette,
                title = stringResource(R.string.settings_theme),
                body = stringResource(currentAppearance.labelRes),
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
        item { SectionHeader(title = stringResource(R.string.settings_language)) }
        item {
            QingClickableCard(
                onClick = { showLanguageDialog = true },
                modifier = Modifier.fillMaxWidth(),
            ) {
                QingListItem(
                    leadingContent = {
                        Icon(Icons.Outlined.Language, contentDescription = null)
                    },
                    headlineContent = { Text(stringResource(R.string.settings_language)) },
                    supportingContent = {
                        Text(stringResource(R.string.settings_language_current, stringResource(currentLanguage.labelRes)))
                    },
                )
            }
        }
        item { QingDivider() }
        item { SectionHeader(title = stringResource(R.string.settings_about)) }
        item {
            QingListItem(
                leadingContent = { Icon(Icons.Outlined.Info, contentDescription = null) },
                headlineContent = { Text(stringResource(R.string.settings_qing_android)) },
                supportingContent = { Text(stringResource(R.string.settings_about_body)) },
            )
        }
        item {
            QingListItem(
                leadingContent = { Icon(Icons.Outlined.Check, contentDescription = null) },
                headlineContent = { Text(stringResource(R.string.settings_version)) },
                supportingContent = {
                    Text(stringResource(R.string.settings_version_body, BuildConfig.VERSION_NAME))
                },
            )
        }
        item {
            QingStatusText(
                text = stringResource(R.string.settings_scope_note),
                modifier = Modifier.padding(top = 4.dp, bottom = 20.dp),
            )
        }
    }
    if (showLanguageDialog) {
        AlertDialog(
            onDismissRequest = { showLanguageDialog = false },
            title = { Text(stringResource(R.string.language_choose_title)) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    AppLanguage.entries.forEach { language ->
                        QingClickableCard(
                            onClick = {
                                showLanguageDialog = false
                                AppLanguageManager.apply(language)
                            },
                            modifier = Modifier.fillMaxWidth(),
                            selected = language == currentLanguage,
                        ) {
                            Row(
                                modifier = Modifier.padding(horizontal = 14.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Text(
                                    text = stringResource(language.labelRes),
                                    modifier = Modifier.weight(1f),
                                )
                                if (language == currentLanguage) {
                                    Icon(
                                        imageVector = Icons.Outlined.Check,
                                        contentDescription = stringResource(R.string.selected),
                                        tint = MaterialTheme.colorScheme.primary,
                                    )
                                }
                            }
                        }
                    }
                }
            },
            confirmButton = {
                TextButton(onClick = { showLanguageDialog = false }) {
                    Text(stringResource(R.string.cancel))
                }
            },
        )
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
                    text = stringResource(theme.labelRes),
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
                )
                Text(
                    text = stringResource(theme.descriptionRes),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            if (selected) {
                Icon(
                    imageVector = Icons.Outlined.Check,
                    contentDescription = stringResource(R.string.selected),
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
                    contentDescription = stringResource(R.string.open_item_content_description, title),
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
