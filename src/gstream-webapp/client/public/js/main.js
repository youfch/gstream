import * as Config from "./config.js";
import {addServer, removeServer, reset, readServersFromLocalStorage} from "./icesettings.js";
import { t, applyLocale, createLangSwitcher } from "./i18n.js";

const addButton = document.querySelector('button#add');
const removeButton = document.querySelector('button#remove');
const resetButton = document.querySelector('button#reset');
const startupDiv = document.getElementById("startup");

addButton.onclick = addServer;
removeButton.onclick = removeServer;
resetButton.onclick = reset;
startupDiv.innerHTML = "";

const displayConfig = async () => {
  const res = await Config.getServerConfig();
  if (res.useWebSocket) {
    startupDiv.innerHTML += `<li>${t('config.protocol')} : <b>WebSocket</b></li>`;
  } else {
    startupDiv.innerHTML += `<li>${t('config.protocol')} : <b>HTTP</b></li>`;
  }

  const mode = res.startupMode.replace(/^./, res.startupMode[0].toUpperCase());
  startupDiv.innerHTML += `<li>${t('config.mode')} : <b>${mode}</b></li>`;
};

// Apply locale and create language switcher
applyLocale();
displayConfig();
readServersFromLocalStorage();

const langContainer = document.getElementById('langSwitcher');
if (langContainer) {
  createLangSwitcher(langContainer, () => {
    // Re-render dynamic content on language change
    startupDiv.innerHTML = "";
    displayConfig();
  });
}
