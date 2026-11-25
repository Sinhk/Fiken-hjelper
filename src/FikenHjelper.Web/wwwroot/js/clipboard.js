// Kopierer både HTML og ren tekst, slik at innliming i Word/Google Docs gir en tabell,
// mens enkle tekstfelt får tabulatorseparert tekst.
export async function copyRichText(html, text) {
    if (navigator.clipboard && window.ClipboardItem) {
        const item = new ClipboardItem({
            "text/html": new Blob([html], { type: "text/html" }),
            "text/plain": new Blob([text], { type: "text/plain" })
        });
        await navigator.clipboard.write([item]);
        return;
    }

    await copyText(text);
}

export async function copyText(text) {
    await navigator.clipboard.writeText(text);
}
