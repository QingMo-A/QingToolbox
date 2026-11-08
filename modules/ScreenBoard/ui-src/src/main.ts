import '@qingtoolbox/module-ui/module.css'
import './styles.css'
import { createApp } from 'vue'
import { adoptHostAppearance } from '@qingtoolbox/module-ui'
import { getContext } from './bridge'
import App from './App.vue'
import Canvas from './Canvas.vue'
import Tools from './Tools.vue'
const surface=new URLSearchParams(location.search).get('surface')
if(surface==='canvas'||surface==='tools'){
  document.documentElement.classList.add('native-board',`surface-${surface}`)
  createApp(surface==='canvas'?Canvas:Tools).mount('#app')
}else void adoptHostAppearance(getContext).then(()=>createApp(App).mount('#app'))
