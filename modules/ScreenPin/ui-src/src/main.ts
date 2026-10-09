import { createApp } from 'vue'
import App from './App.vue'
import '@qingtoolbox/module-ui/module.css'
// After the shared sheet, so the board's own rules win ties.
import './styles.css'
import { adoptHostAppearance } from '@qingtoolbox/module-ui'
import { getContext } from './bridge'

void adoptHostAppearance(getContext).then(() => createApp(App).mount('#app'))
