import { createApp } from 'vue'
import App from './App.vue'
import './styles.css'
import '@qingtoolbox/module-ui/module.css'
import { adoptHostAppearance } from '@qingtoolbox/module-ui'
import { getContext } from './bridge'

void adoptHostAppearance(getContext).then(() => createApp(App).mount('#app'))
