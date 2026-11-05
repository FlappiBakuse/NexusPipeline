import baah from '../assets/script-icons/baah.ico';
import bettergi from '../assets/script-icons/bettergi.ico';
import maaend from '../assets/script-icons/maaend.png';
import maas from '../assets/script-icons/maas.ico';
import march7th from '../assets/script-icons/march7th.ico';
import oknte from '../assets/script-icons/oknte.ico';
import okww from '../assets/script-icons/okww.ico';
import zzzonedragon from '../assets/script-icons/zzzonedragon.ico';
import generic from '../assets/script-icons/generic.ico';
import maaFramework from '../assets/script-icons/maa-framework.png';

const icons: Readonly<Record<string, string>> = { baah, bettergi, maaend, maas, march7th, oknte, okww, zzzonedragon, 'maa-framework': maaFramework };
export const scriptIcon = (extensionId?: string): string => extensionId ? icons[extensionId] || generic : generic;
