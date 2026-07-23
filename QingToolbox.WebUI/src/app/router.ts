import { createRouter, createWebHashHistory } from 'vue-router'
import DevelopmentHomePage from '../pages/DevelopmentHomePage.vue'
export const router = createRouter({ history: createWebHashHistory(), routes: [{ path: '/', component: DevelopmentHomePage }] })
