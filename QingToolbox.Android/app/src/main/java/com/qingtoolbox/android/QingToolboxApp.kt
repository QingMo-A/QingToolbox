package com.qingtoolbox.android

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.annotation.StringRes
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
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.Add
import androidx.compose.material.icons.outlined.AutoAwesome
import androidx.compose.material.icons.outlined.Check
import androidx.compose.material.icons.outlined.ChevronRight
import androidx.compose.material.icons.outlined.DeleteOutline
import androidx.compose.material.icons.outlined.DevicesOther
import androidx.compose.material.icons.outlined.Extension
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.Home
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.outlined.Language
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavHostController
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController

/** Number of module tiles per row on the home grid. */
private const val MODULE_GRID_COLUMNS = 2

/** Route pattern of a module's page. The pattern is what the navigation graph reports. */
private const val MODULE_DETAIL_ROUTE = "shell/module/{moduleId}"

private const val MODULE_ID_ARGUMENT = "moduleId"

private fun moduleDetailRoute(id: String): String = "shell/module/$id"

/**
 * Destinations owned by the shell itself.
 *
 * These are the frame — the shell never adds an entry here to ship a feature. Features
 * arrive as imported modules and are rendered by the navigation graph below, so the shell
 * stays a shell.
 */
private sealed class ShellDestination(
    val route: String,
    @StringRes val labelRes: Int,
    val icon: ImageVector,
) {
    data object Home : ShellDestination("shell/home", R.string.nav_home, Icons.Outlined.Home)
    data object Modules : ShellDestination("shell/modules", R.string.nav_modules, Icons.Outlined.Extension)
    data object Devices : ShellDestination("shell/devices", R.string.nav_devices, Icons.Outlined.DevicesOther)
    data object Settings : ShellDestination("shell/settings", R.string.nav_settings, Icons.Outlined.Settings)
}

private val shellDestinations = listOf(
    ShellDestination.Home,
    ShellDestination.Modules,
    ShellDestination.Devices,
    ShellDestination.Settings,
)

@Composable
fun QingToolboxApp(viewModel: QingToolboxViewModel) {
    val uiState by viewModel.uiState.collectAsStateWithLifecycle()

    QingToolboxTheme(appearance = uiState.appearance) {
        QingToolboxShell(viewModel = viewModel, uiState = uiState)
    }
}

@Composable
private fun QingToolboxShell(
    viewModel: QingToolboxViewModel,
    uiState: QingToolboxUiState,
) {
    val navController = rememberNavController()
    val context = LocalContext.current
    val backStackEntry by navController.currentBackStackEntryAsState()
    val currentRoute = backStackEntry?.destination?.route
    val openModuleId = backStackEntry?.arguments?.getString(MODULE_ID_ARGUMENT)
    val openModule = viewModel.moduleFor(openModuleId)
    val currentShell = shellDestinations.firstOrNull { it.route == currentRoute }
        ?: ShellDestination.Home
    // A module is a payload of the shell, so opening one keeps the Modules entry selected.
    val selectedShell = if (openModuleId != null) ShellDestination.Modules else currentShell
    val languageTag = currentLanguageTag()
    val snackbarHostState = remember { SnackbarHostState() }
    val importLauncher = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) viewModel.importModule(uri)
    }

    val moduleThemeCss = rememberMobileModuleThemeCss()
    LaunchedEffect(moduleThemeCss) { viewModel.runtime.updateTheme(moduleThemeCss) }

    // The transfer session is process-scoped, so the shell and the Devices screen share
    // one instance rather than each opening its own. The folder dialog is hosted here
    // because the button that opens it lives in the top bar.
    val transferSession = remember(context) { QingTransferProcessSessionStore.get(context) }
    var showReceiveSettings by remember { mutableStateOf(false) }

    uiState.message?.let { message ->
        LaunchedEffect(message.token) {
            snackbarHostState.showSnackbar(message.text)
            viewModel.consumeMessage(message.token)
        }
    }

    Scaffold(
        topBar = {
            QingTopAppBar(
                navigationIcon = if (openModuleId != null) {
                    {
                        IconButton(onClick = { navController.popBackStack() }) {
                            Icon(
                                imageVector = Icons.AutoMirrored.Outlined.ArrowBack,
                                contentDescription = stringResource(R.string.back),
                            )
                        }
                    }
                } else {
                    null
                },
                title = {
                    Text(
                        text = openModule?.displayName(languageTag)
                            ?: openModuleId
                            ?: stringResource(currentShell.labelRes),
                        style = MaterialTheme.typography.titleLarge,
                        fontWeight = FontWeight.SemiBold,
                    )
                },
                actions = when {
                    currentShell == ShellDestination.Modules && openModuleId == null -> {
                        {
                            if (uiState.busy) {
                                CircularProgressIndicator(
                                    modifier = Modifier
                                        .padding(horizontal = 14.dp)
                                        .size(20.dp),
                                    strokeWidth = 2.dp,
                                )
                            } else {
                                IconButton(onClick = { importLauncher.launch(arrayOf("*/*")) }) {
                                    Icon(
                                        imageVector = Icons.Outlined.Add,
                                        contentDescription = stringResource(R.string.modules_import_action),
                                    )
                                }
                            }
                        }
                    }
                    currentShell == ShellDestination.Devices && openModuleId == null -> {
                        {
                            // Receive-folder settings live here so the transfer screen itself
                            // stays a list of peers. Folder first, refresh last, matching the
                            // order the two buttons read in.
                            IconButton(onClick = { showReceiveSettings = true }) {
                                Icon(
                                    imageVector = Icons.Outlined.Folder,
                                    contentDescription = stringResource(R.string.qing_transfer_receive_settings),
                                )
                            }
                            IconButton(
                                onClick = {
                                    // A restart is a full foreground-session reset: no stale
                                    // connection may outlive the advertised listener.
                                    transferSession.connection.disconnect()
                                    transferSession.discovery.restart()
                                },
                            ) {
                                Icon(
                                    imageVector = Icons.Outlined.Refresh,
                                    contentDescription = stringResource(R.string.qing_transfer_refresh),
                                )
                            }
                        }
                    }
                    else -> null
                },
            )
        },
        bottomBar = {
            QingNavigationBar {
                shellDestinations.forEach { destination ->
                    QingNavigationBarItem(
                        selected = selectedShell.route == destination.route,
                        onClick = { navigateTo(navController, destination.route) },
                        icon = { Icon(destination.icon, contentDescription = null) },
                        label = { Text(stringResource(destination.labelRes)) },
                    )
                }
            }
        },
        snackbarHost = { SnackbarHost(snackbarHostState) },
        containerColor = MaterialTheme.colorScheme.background,
    ) { innerPadding ->
        NavHost(
            navController = navController,
            startDestination = ShellDestination.Home.route,
            // No transition between shell destinations. A cross-fade keeps the outgoing
            // screen composed for its whole duration, so a tab tap leaves the previous
            // page visible underneath the new one instead of replacing it.
            //
            // The start destination must stay a `String` here. NavHost has overloads taking
            // `String` and `Any`; the `Any` one wants a KClass-registered graph and throws
            // "Cannot find startDestination kotlin.String from NavGraph" when handed a
            // route-based one. Wrapping this argument in anything that erases its static
            // type silently selects the wrong overload.
            enterTransition = { ShellEnterTransition },
            exitTransition = { ShellExitTransition },
            modifier = Modifier
                .fillMaxSize()
                .padding(innerPadding),
        ) {
            composable(ShellDestination.Home.route) {
                val state by viewModel.uiState.collectAsStateWithLifecycle()
                HomeScreen(
                    modules = state.modules,
                    loadedIds = state.loadedIds,
                    onModuleClick = { module -> navigateTo(navController, moduleDetailRoute(module.id)) },
                    onImportClick = { importLauncher.launch(arrayOf("*/*")) },
                )
            }
            composable(ShellDestination.Modules.route) {
                val state by viewModel.uiState.collectAsStateWithLifecycle()
                ModulesScreen(
                    modules = state.modules,
                    loadedIds = state.loadedIds,
                    query = state.query,
                    filter = state.filter,
                    onQueryChange = viewModel::setQuery,
                    onFilterChange = viewModel::setFilter,
                    onModuleClick = { module -> navigateTo(navController, moduleDetailRoute(module.id)) },
                    onImportClick = { importLauncher.launch(arrayOf("*/*")) },
                )
            }
            // The module page reads the module from its own route argument and the state
            // from the shell, so the navigation graph never captures a changing value.
            composable(MODULE_DETAIL_ROUTE) { entry ->
                val state by viewModel.uiState.collectAsStateWithLifecycle()
                val module = state.modules.firstOrNull { it.id == entry.arguments?.getString(MODULE_ID_ARGUMENT) }
                if (module != null) {
                    val loaded = module.id in state.loadedIds
                    ModuleDetailScreen(
                        module = module,
                        loaded = loaded,
                        session = if (loaded) viewModel.runtime.sessionFor(module.id) else null,
                        onLoad = { viewModel.loadModule(module.id) },
                        onUnload = { viewModel.unloadModule(module.id) },
                        onDelete = { viewModel.deleteModule(module.id) },
                    )
                }
            }
            // Device connectivity stays on the shell: it is core plumbing shared with the
            // desktop host rather than an installable module.
            composable(ShellDestination.Devices.route) {
                QingTransferDevicesScreen(
                    showReceiveSettings = showReceiveSettings,
                    onDismissReceiveSettings = { showReceiveSettings = false },
                )
            }
            composable(ShellDestination.Settings.route) {
                val state by viewModel.uiState.collectAsStateWithLifecycle()
                SettingsScreen(
                    currentAppearance = state.appearance,
                    onAppearanceSelected = viewModel::selectAppearance,
                )
            }
        }
    }
}

private fun navigateTo(navController: NavHostController, route: String) {
    navController.navigate(route) {
        popUpTo(navController.graph.startDestinationId) {
            saveState = true
        }
        launchSingleTop = true
        restoreState = true
    }
}

@Composable
private fun currentLanguageTag(): String {
    val locale = LocalConfiguration.current.locales[0]
    return locale.toLanguageTag()
}

@Composable
private fun HomeScreen(
    modules: List<InstalledMobileModule>,
    loadedIds: Set<String>,
    onModuleClick: (InstalledMobileModule) -> Unit,
    onImportClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val languageTag = currentLanguageTag()
    val heroContentColor = if (LocalQingAppearance.current.primaryBrush() != null) {
        MaterialTheme.colorScheme.onPrimary
    } else {
        MaterialTheme.colorScheme.onPrimaryContainer
    }
    val moduleRows = remember(modules) { modules.chunked(MODULE_GRID_COLUMNS) }
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item {
            QingHeroCard(modifier = Modifier.fillMaxWidth()) {
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
                            text = stringResource(
                                R.string.home_status_line,
                                BuildConfig.VERSION_NAME,
                                modules.size,
                            ),
                            style = MaterialTheme.typography.labelMedium,
                            color = heroContentColor,
                        )
                    }
                }
            }
        }
        item { ModuleSectionTitle(stringResource(R.string.nav_modules)) }
        items(moduleRows) { row ->
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                row.forEach { module ->
                    ModuleTile(
                        module = module,
                        loaded = module.id in loadedIds,
                        languageTag = languageTag,
                        onClick = { onModuleClick(module) },
                        modifier = Modifier.weight(1f),
                    )
                }
                // Keeps a short last row aligned with the grid columns above it.
                repeat(MODULE_GRID_COLUMNS - row.size) {
                    Spacer(Modifier.weight(1f))
                }
            }
        }
        if (modules.isEmpty()) {
            item {
                ModuleEmptyCard(
                    title = stringResource(R.string.modules_empty_title),
                    body = stringResource(R.string.modules_empty_body),
                    actionLabel = stringResource(R.string.modules_import_action),
                    onAction = onImportClick,
                )
            }
        }
    }
}

/** The module list, with the one search box and the loading-state filter above it. */
@Composable
private fun ModulesScreen(
    modules: List<InstalledMobileModule>,
    loadedIds: Set<String>,
    query: String,
    filter: MobileModuleFilter,
    onQueryChange: (String) -> Unit,
    onFilterChange: (MobileModuleFilter) -> Unit,
    onModuleClick: (InstalledMobileModule) -> Unit,
    onImportClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val languageTag = currentLanguageTag()
    val visible = remember(modules, query, filter, loadedIds, languageTag) {
        MobileModuleQuery.apply(modules, query, filter, loadedIds, languageTag)
    }
    val counts = remember(modules, loadedIds) {
        mapOf(
            MobileModuleFilter.ALL to modules.size,
            MobileModuleFilter.LOADED to modules.count { it.id in loadedIds },
            MobileModuleFilter.NOT_LOADED to modules.count { it.id !in loadedIds },
        )
    }

    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item { ModuleSearchField(value = query, onValueChange = onQueryChange) }
        item {
            ModuleFilterRow(
                selected = filter,
                counts = counts,
                onSelect = onFilterChange,
            )
        }
        if (visible.isEmpty()) {
            item {
                if (modules.isEmpty()) {
                    ModuleEmptyCard(
                        title = stringResource(R.string.modules_empty_title),
                        body = stringResource(R.string.modules_empty_body),
                        actionLabel = stringResource(R.string.modules_import_action),
                        onAction = onImportClick,
                    )
                } else {
                    ModuleEmptyCard(
                        title = stringResource(R.string.modules_no_match_title),
                        body = stringResource(R.string.modules_no_match_body),
                    )
                }
            }
        } else {
            items(visible, key = { it.id }) { module ->
                ModuleListRow(
                    module = module,
                    loaded = module.id in loadedIds,
                    languageTag = languageTag,
                    onClick = { onModuleClick(module) },
                )
            }
        }
    }
}

/**
 * One module's page.
 *
 * An unloaded module shows what it declares — version, id, size, the capabilities it will
 * ask for — and offers to load or delete it. A loaded module replaces that explanation with
 * the module itself, and keeps a compact action bar so it can still be unloaded or removed.
 */
@Composable
private fun ModuleDetailScreen(
    module: InstalledMobileModule,
    loaded: Boolean,
    session: MobileModuleSession?,
    onLoad: () -> Unit,
    onUnload: () -> Unit,
    onDelete: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val languageTag = currentLanguageTag()
    var confirmDelete by remember(module.id) { mutableStateOf(false) }

    if (loaded && session != null) {
        Column(modifier = modifier.fillMaxSize()) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 10.dp),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                ModuleGlyph(module = module, size = 34.dp)
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = module.displayName(languageTag),
                        style = MaterialTheme.typography.titleSmall,
                        fontWeight = FontWeight.SemiBold,
                        maxLines = 1,
                    )
                    Text(
                        text = stringResource(R.string.modules_version_line, module.version),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                TextButton(onClick = onUnload) {
                    Text(stringResource(R.string.modules_unload))
                }
                IconButton(onClick = { confirmDelete = true }) {
                    Icon(
                        imageVector = Icons.Outlined.DeleteOutline,
                        contentDescription = stringResource(R.string.modules_delete),
                        tint = MaterialTheme.colorScheme.error,
                    )
                }
            }
            ModuleHairline()
            MobileModuleWebView(
                session = session,
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f),
            )
        }
    } else {
        LazyColumn(
            modifier = modifier.fillMaxSize(),
            contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            item {
                QingCard(modifier = Modifier.fillMaxWidth()) {
                    Column(modifier = Modifier.padding(18.dp)) {
                        Row(
                            horizontalArrangement = Arrangement.spacedBy(14.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            ModuleGlyph(module = module, size = 52.dp)
                            Column(modifier = Modifier.weight(1f)) {
                                Text(
                                    text = module.displayName(languageTag),
                                    style = MaterialTheme.typography.titleLarge,
                                    fontWeight = FontWeight.SemiBold,
                                )
                                Spacer(Modifier.height(4.dp))
                                ModuleLoadChip(loaded = false)
                            }
                        }
                        val description = module.description(languageTag)
                        if (description.isNotBlank()) {
                            Spacer(Modifier.height(12.dp))
                            Text(
                                text = description,
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }
            }
            item {
                QingCard(modifier = Modifier.fillMaxWidth()) {
                    Column(modifier = Modifier.padding(horizontal = 18.dp, vertical = 10.dp)) {
                        ModuleDetailRow(
                            label = stringResource(R.string.modules_detail_id),
                            value = module.id,
                        )
                        ModuleHairline()
                        ModuleDetailRow(
                            label = stringResource(R.string.modules_detail_version),
                            value = module.version,
                        )
                        ModuleHairline()
                        ModuleDetailRow(
                            label = stringResource(R.string.modules_detail_size),
                            value = formatModuleSize(module.sizeBytes),
                        )
                        ModuleHairline()
                        ModuleDetailRow(
                            label = stringResource(R.string.modules_detail_runtime),
                            value = stringResource(R.string.modules_runtime_web),
                        )
                    }
                }
            }
            item { ModuleSectionTitle(stringResource(R.string.modules_detail_capabilities)) }
            item {
                QingCard(modifier = Modifier.fillMaxWidth()) {
                    Column(modifier = Modifier.padding(18.dp)) {
                        ModuleCapabilityRow(capabilities = module.manifest.capabilities)
                    }
                }
            }
            item {
                QingPrimaryButton(
                    onClick = onLoad,
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Text(stringResource(R.string.modules_load))
                }
            }
            item {
                QingSecondaryButton(
                    onClick = { confirmDelete = true },
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Icon(
                        imageVector = Icons.Outlined.DeleteOutline,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.error,
                        modifier = Modifier.size(18.dp),
                    )
                    Spacer(Modifier.size(8.dp))
                    Text(
                        text = stringResource(R.string.modules_delete),
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            }
        }
    }

    if (confirmDelete) {
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text(stringResource(R.string.modules_delete_title)) },
            text = {
                Text(
                    stringResource(
                        R.string.modules_delete_body,
                        module.displayName(languageTag),
                    ),
                )
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        confirmDelete = false
                        onDelete()
                    },
                ) {
                    Text(
                        text = stringResource(R.string.modules_delete),
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            },
            dismissButton = {
                TextButton(onClick = { confirmDelete = false }) {
                    Text(stringResource(R.string.cancel))
                }
            },
        )
    }
}

/** Sizes are shown in the unit the user reads in a file manager. */
private fun formatModuleSize(bytes: Long): String = when {
    bytes < 1024 -> "$bytes B"
    bytes < 1024 * 1024 -> "%.1f KB".format(bytes / 1024.0)
    else -> "%.2f MB".format(bytes / (1024.0 * 1024.0))
}

@Composable
private fun SettingsScreen(
    currentAppearance: AppearanceTheme,
    onAppearanceSelected: (AppearanceTheme) -> Unit,
    modifier: Modifier = Modifier,
) {
    var showThemeDialog by remember { mutableStateOf(false) }
    var showLanguageDialog by remember { mutableStateOf(false) }
    val currentLanguage = AppLanguageManager.current()
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item { ModuleSectionTitle(stringResource(R.string.settings_appearance)) }
        item {
            QingClickableCard(
                onClick = { showThemeDialog = true },
                modifier = Modifier.fillMaxWidth(),
            ) {
                QingListItem(
                    leadingContent = {
                        ThemeSwatch(theme = currentAppearance, selected = true)
                    },
                    headlineContent = { Text(stringResource(R.string.settings_theme)) },
                    supportingContent = {
                        Text(
                            stringResource(
                                R.string.settings_theme_current,
                                stringResource(currentAppearance.labelRes),
                            ),
                        )
                    },
                    trailingContent = { DisclosureIndicator() },
                )
            }
        }
        item { ModuleSectionTitle(stringResource(R.string.settings_language)) }
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
                    trailingContent = { DisclosureIndicator() },
                )
            }
        }
        item { QingDivider() }
        item { ModuleSectionTitle(stringResource(R.string.settings_about)) }
        item {
            QingListItem(
                leadingContent = { Icon(Icons.Outlined.Info, contentDescription = null) },
                headlineContent = { Text(stringResource(R.string.settings_qing_android)) },
                supportingContent = {
                    Text(stringResource(R.string.settings_version_body, BuildConfig.VERSION_NAME))
                },
            )
        }
    }
    if (showThemeDialog) {
        ThemeChooserDialog(
            current = currentAppearance,
            onSelect = { theme -> onAppearanceSelected(theme) },
            onDismiss = { showThemeDialog = false },
        )
    }
    if (showLanguageDialog) {
        LanguageChooserDialog(
            current = currentLanguage,
            onSelect = { language ->
                showLanguageDialog = false
                AppLanguageManager.apply(language)
            },
            onDismiss = { showLanguageDialog = false },
        )
    }
}

/**
 * One shared shape for both appearance pickers.
 *
 * Both settings collapse into a single row that opens this list, so the settings page
 * keeps one line per choice instead of spending a screen on every option.
 */
@Composable
private fun ChooserDialog(
    title: String,
    confirmLabel: String,
    onDismiss: () -> Unit,
    content: @Composable ColumnScope.() -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            Column(
                modifier = Modifier.verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(8.dp),
                content = content,
            )
        },
        confirmButton = {
            TextButton(onClick = onDismiss) { Text(confirmLabel) }
        },
    )
}

@Composable
private fun ThemeChooserDialog(
    current: AppearanceTheme,
    onSelect: (AppearanceTheme) -> Unit,
    onDismiss: () -> Unit,
) {
    ChooserDialog(
        title = stringResource(R.string.settings_theme_choose_title),
        confirmLabel = stringResource(R.string.done),
        onDismiss = onDismiss,
    ) {
        // Applying a theme is immediate and the dialog stays open, so this preview is a
        // live sample of the theme currently applied.
        QingThemePreview(theme = current, modifier = Modifier.fillMaxWidth())
        Text(
            text = stringResource(R.string.settings_theme_preview),
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        AppearanceTheme.entries.forEach { theme ->
            ThemeOptionRow(
                theme = theme,
                selected = theme == current,
                onClick = { onSelect(theme) },
            )
        }
    }
}

@Composable
private fun LanguageChooserDialog(
    current: AppLanguage,
    onSelect: (AppLanguage) -> Unit,
    onDismiss: () -> Unit,
) {
    ChooserDialog(
        title = stringResource(R.string.language_choose_title),
        confirmLabel = stringResource(R.string.cancel),
        onDismiss = onDismiss,
    ) {
        AppLanguage.entries.forEach { language ->
            QingClickableCard(
                onClick = { onSelect(language) },
                modifier = Modifier.fillMaxWidth(),
                selected = language == current,
            ) {
                Row(
                    modifier = Modifier.padding(horizontal = 14.dp, vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = stringResource(language.labelRes),
                        modifier = Modifier.weight(1f),
                    )
                    if (language == current) {
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
}

@Composable
private fun ThemeOptionRow(
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
            modifier = Modifier.padding(horizontal = 14.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            ThemeSwatch(theme = theme, selected = selected)
            // The row stays a single line: the picker is a list of choices, and the
            // swatch plus the live preview above already show what a theme looks like.
            Text(
                text = stringResource(theme.labelRes),
                style = MaterialTheme.typography.titleMedium,
                fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
                modifier = Modifier.weight(1f),
            )
            if (selected) {
                Icon(
                    imageVector = Icons.Outlined.Check,
                    contentDescription = stringResource(R.string.selected),
                    tint = MaterialTheme.colorScheme.primary,
                )
            }
        }
    }
}

@Composable
private fun ThemeSwatch(theme: AppearanceTheme, selected: Boolean) {
    QingIconSurface(
        modifier = Modifier.size(if (selected) 44.dp else 40.dp),
        containerColor = theme.swatchColor,
    ) {}
}

@Composable
private fun DisclosureIndicator() {
    Icon(
        imageVector = Icons.Outlined.ChevronRight,
        contentDescription = null,
        tint = MaterialTheme.colorScheme.onSurfaceVariant,
    )
}
