import { createApp } from 'vue';
import { registerElements } from './ui/register-elements';
import App from './app/App.vue';
import FormSelect from './components/FormSelect';
import FormDate from './components/FormDate.vue';
import FormNumber from './components/FormNumber.vue';
import PageActions from './components/PageActions.vue';
import './styles/theme.css';
import './ui/theme.css';
import './styles/workspace.css';
import './styles/app-shell.css';
import './styles/painted-controls.css';
registerElements();
createApp(App)
  .component('FormSelect', FormSelect)
  .component('FormDate', FormDate)
  .component('PageActions', PageActions)
  .component('FormNumber', FormNumber)
  .mount('#app');
