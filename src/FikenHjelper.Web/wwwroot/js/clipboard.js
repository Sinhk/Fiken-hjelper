// Kopiering til utklippstavlen for kvalitetssjekken.
// Returnerer "rich" (HTML + tekst), "text" (bare tekst) eller "insecure" når nettleseren
// ikke gir tilgang (siden er åpnet over http fra en annen maskin enn localhost).

export function isAvailable() {
    return !!(navigator.clipboard && window.isSecureContext);
}

export async function copyRichText(html, text) {
    if (!isAvailable()) {
        return fallbackCopy(text) ? "text" : "insecure";
    }

    if (window.ClipboardItem) {
        try {
            const item = new ClipboardItem({
                "text/html": new Blob([html], { type: "text/html" }),
                "text/plain": new Blob([text], { type: "text/plain" })
            });
            await navigator.clipboard.write([item]);
            return "rich";
        } catch {
            // Faller tilbake til ren tekst under (f.eks. når HTML ikke er tillatt).
        }
    }

    await navigator.clipboard.writeText(text);
    return "text";
}

export async function copyText(text) {
    if (!isAvailable()) {
        return fallbackCopy(text) ? "text" : "insecure";
    }

    await navigator.clipboard.writeText(text);
    return "text";
}

// Siste utvei uten Clipboard API: markér tekst i et skjult felt og bruk execCommand.
function fallbackCopy(text) {
    const area = document.createElement("textarea");
    area.value = text;
    area.setAttribute("readonly", "");
    area.style.position = "fixed";
    area.style.opacity = "0";
    document.body.appendChild(area);
    area.select();
    try {
        return document.execCommand("copy");
    } catch {
        return false;
    } finally {
        document.body.removeChild(area);
    }
}
