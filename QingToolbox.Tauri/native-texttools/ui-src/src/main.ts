import { createApp } from 'vue'
import App from './App.vue'
import './styles.css'
// Last, and deliberately so. The shell appends its preset stylesheet after its
// component styles for the same reason: a preset and a component rule that
// touch the same property usually have equal specificity, so only source order
// decides, and the appearance has to be the one that wins.
import './theme.css'
import { getContext } from './bridge'
import { adoptHostAppearance } from './hostTheme'

// Mounted only once the appearance has been settled. Otherwise the window
// paints in whatever the stylesheets default to and then corrects itself in
// front of the user. Inside the host this costs a microtask — the injected
// script has already answered — and outside it costs one request.
void adoptHostAppearance(getContext).then(() => createApp(App).mount('#app'))
