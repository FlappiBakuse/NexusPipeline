import { createPinia } from "pinia";
import { createApp } from "vue";
import App from "./app/App.vue";
import { router } from "./router";
import { registerNexusElements } from "./ui/register";
import { initializeDesktopPreferences } from './platform/desktop';
import { setLocale } from './platform/i18n';
import "./styles/tokens.css";
import "./styles/app.css";
import "./styles/shell.css";
import "./styles/platform-tooltip.css";
import "./styles/workspace.css";

registerNexusElements();
void initializeDesktopPreferences().then(async preferences=>{
  if(preferences)await setLocale(preferences.locale);
}).catch(()=>{document.documentElement.dataset.desktopPreferences='unavailable';}).finally(()=>{
  createApp(App)
  .use(createPinia())
  .use(router)
  .mount("#app");
});
