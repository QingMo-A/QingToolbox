import { createApp } from 'vue'
import App from './App.vue'
import '@qingtoolbox/module-ui/module.css'
import './style.css'
import { adoptHostAppearance } from '@qingtoolbox/module-ui'
import { getContext } from './bridge'

void adoptHostAppearance(getContext).then(() => createApp(App).mount('#app'))
