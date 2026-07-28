import { createRouter, createWebHashHistory } from 'vue-router'
import HomePage from '../pages/HomePage.vue'
import ModulesPage from '../pages/ModulesPage.vue'
import RunningModulesPage from '../pages/RunningModulesPage.vue'
import LogsPage from '../pages/LogsPage.vue'
import SettingsPage from '../pages/SettingsPage.vue'
import DevelopmentDiagnosticsPage from '../pages/DevelopmentHomePage.vue'
import type { TranslationKey } from '../localization/messages/en-US'

export const routeTitleKeyByPath = {
  '/': 'navigation.home',
  '/modules': 'navigation.modules',
  '/running': 'navigation.running',
  '/logs': 'navigation.logs',
  '/settings': 'navigation.settings',
  '/diagnostics': 'navigation.diagnostics',
} as const satisfies Record<string, TranslationKey>

export const router = createRouter({ history: createWebHashHistory(), scrollBehavior:()=>({top:0}), routes: [
  { path: '/', component: HomePage, meta:{titleKey:routeTitleKeyByPath['/']} },
  { path: '/modules', component: ModulesPage, meta:{titleKey:routeTitleKeyByPath['/modules']} },
  { path: '/running', component: RunningModulesPage, meta:{titleKey:routeTitleKeyByPath['/running']} },
  { path: '/logs', component: LogsPage, meta:{titleKey:routeTitleKeyByPath['/logs']} },
  { path: '/settings', component: SettingsPage, meta:{titleKey:routeTitleKeyByPath['/settings']} },
  { path: '/diagnostics', component: DevelopmentDiagnosticsPage, meta:{titleKey:routeTitleKeyByPath['/diagnostics']} }
] })
