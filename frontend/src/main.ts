import { createPinia } from "pinia";
import { createApp } from "vue";
import App from "./app/App.vue";
import { router } from "./router";
import { registerNexusElements } from "./ui/register";
import "./styles/tokens.css";
import "./styles/app.css";
import "./styles/shell.css";

registerNexusElements();

createApp(App)
  .use(createPinia())
  .use(router)
  .mount("#app");
