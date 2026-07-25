import { createRouter, createWebHashHistory } from 'vue-router'
import HomePage from '../pages/HomePage.vue'
import ModulesPage from '../pages/ModulesPage.vue'
import RunningModulesPage from '../pages/RunningModulesPage.vue'
import LogsPage from '../pages/LogsPage.vue'
import DevelopmentDiagnosticsPage from '../pages/DevelopmentHomePage.vue'
export const router = createRouter({ history: createWebHashHistory(), scrollBehavior:()=>({top:0}), routes: [
  { path: '/', component: HomePage, meta:{title:'Home'} },
  { path: '/modules', component: ModulesPage, meta:{title:'Modules'} },
  { path: '/running', component: RunningModulesPage, meta:{title:'Running modules'} },
  { path: '/logs', component: LogsPage, meta:{title:'Session logs'} },
  { path: '/diagnostics', component: DevelopmentDiagnosticsPage, meta:{title:'Development diagnostics'} }
] })
