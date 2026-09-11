export function activate(host) {
  const registration = host.slots.register("settings.cards", ({ element }) => {
    element.replaceChildren();
    const card = document.createElement("nxp-card");
    card.dataset.testid = "frontend-fixture-card";
    card.textContent = "前端插件 fixture";
    element.append(card);
    return () => element.replaceChildren();
  });
  return () => registration.dispose();
}
