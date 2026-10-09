// The shared design system first, then the page, then the components: each
// later sheet wins equal-specificity ties against the shared controls.
import '@qingtoolbox/module-ui/module.css'
import './styles.css'
import { createApp } from 'vue'
import App from './App.vue'
import { adoptHostAppearance } from '@qingtoolbox/module-ui'
import { getContext } from './bridge'

// The appearance is adopted before the first paint so the page never flashes
// the wrong theme when the host is in dark mode.
void adoptHostAppearance(getContext).then(() => createApp(App).mount('#app'))
