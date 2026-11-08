import '@qingtoolbox/module-ui/module.css'
import './styles.css'
import { createApp } from 'vue'
import { adoptHostAppearance } from '@qingtoolbox/module-ui'
import { getContext } from './bridge'
import App from './App.vue'
void adoptHostAppearance(getContext).then(() => createApp(App).mount('#app'))
