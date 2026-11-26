using FikenHjelper.Core.Text;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace FikenHjelper.Web.Components.Shared;

/// <summary>
/// Norske tekster (bokmål) for MudBlazors innebygde etiketter, bl.a. tilgjengelighetstekster på knapper
/// i datovelger, tabell og dialoger. Nøkler som ikke er med, faller tilbake til MudBlazors engelske tekst.
/// </summary>
public sealed class NorwegianMudLocalizer : MudLocalizer
{
    private static readonly Dictionary<string, string> Texts = new()
    {
        ["MudAlert_Close"] = "Lukk",
        ["MudChip_Close"] = "Fjern",
        ["MudDialog_Close"] = "Lukk",
        ["MudSnackbar_Close"] = "Lukk",
        ["MudInput_Clear"] = "Tøm",
        ["MudInput_Increment"] = "Øk",
        ["MudInput_Decrement"] = "Reduser",
        ["MudSelect_SelectAll"] = "Velg alle",
        ["MudFormComponent_Required"] = "Påkrevd",

        ["MudBaseDatePicker_Open"] = "Åpne datovelger",
        ["MudBaseDatePicker_PrevMonth"] = "Forrige måned {0}",
        ["MudBaseDatePicker_NextMonth"] = "Neste måned {0}",
        ["MudBaseDatePicker_PrevYear"] = "Forrige år {0}",
        ["MudBaseDatePicker_NextYear"] = "Neste år {0}",
        ["MudDateRangePicker_Start"] = "Fra dato",
        ["MudDateRangePicker_End"] = "Til dato",
        ["MudTimePicker_Open"] = "Åpne klokkevelger",

        ["MudTabs_AddTab"] = "Legg til fane",
        ["MudTabs_CloseTab"] = "Lukk fane",
        ["MudTabs_ScrollLeft"] = "Bla faner til venstre",
        ["MudTabs_ScrollRight"] = "Bla faner til høyre",
        ["MudTabs_ScrollUp"] = "Bla faner opp",
        ["MudTabs_ScrollDown"] = "Bla faner ned",

        ["MudFileUpload_Button"] = "Velg filer",
        ["MudFileUpload_DragAndDrop"] = "Slipp filer her, eller klikk for å velge",
        ["MudFileUpload_MaximumFileCountExceeded"] = "Du valgte {0} filer, men maks {1} er tillatt.",
        ["MudFileUpload_FileSizeError"] = "Filen «{0}» er for stor (maks {1} byte).",

        ["MudDataGrid_AddFilter"] = "Legg til filter",
        ["MudDataGrid_Apply"] = "Bruk",
        ["MudDataGrid_Cancel"] = "Avbryt",
        ["MudDataGrid_Clear"] = "Tøm",
        ["MudDataGrid_ClearFilter"] = "Fjern filter",
        ["MudDataGrid_Column"] = "Kolonne",
        ["MudDataGrid_Columns"] = "Kolonner",
        ["MudDataGrid_Contains"] = "inneholder",
        ["MudDataGrid_EndsWith"] = "slutter med",
        ["MudDataGrid_Equals"] = "er lik",
        ["MudDataGrid_False"] = "usann",
        ["MudDataGrid_Filter"] = "Filter",
        ["MudDataGrid_FilterValue"] = "Filterverdi",
        ["MudDataGrid_Hide"] = "Skjul",
        ["MudDataGrid_HideAll"] = "Skjul alle",
        ["MudDataGrid_Is"] = "er",
        ["MudDataGrid_IsAfter"] = "er etter",
        ["MudDataGrid_IsAnyOf"] = "er en av",
        ["MudDataGrid_IsBefore"] = "er før",
        ["MudDataGrid_IsEmpty"] = "er tom",
        ["MudDataGrid_IsNot"] = "er ikke",
        ["MudDataGrid_IsNotEmpty"] = "er ikke tom",
        ["MudDataGrid_IsOnOrAfter"] = "er på eller etter",
        ["MudDataGrid_IsOnOrBefore"] = "er på eller før",
        ["MudDataGrid_Loading"] = "Laster …",
        ["MudDataGrid_NotContains"] = "inneholder ikke",
        ["MudDataGrid_NotEquals"] = "er ikke lik",
        ["MudDataGrid_OpenFilters"] = "Åpne filter",
        ["MudDataGrid_Operator"] = "Operator",
        ["MudDataGrid_RefreshData"] = "Oppdater data",
        ["MudDataGrid_RemoveFilter"] = "Fjern filter",
        ["MudDataGrid_Save"] = "Lagre",
        ["MudDataGrid_SelectAllRows"] = "Velg alle rader",
        ["MudDataGrid_SelectRow"] = "Velg rad",
        ["MudDataGrid_ShowAll"] = "Vis alle",
        ["MudDataGrid_ShowColumnOptions"] = "Kolonnevalg",
        ["MudDataGrid_Sort"] = "Sorter",
        ["MudDataGrid_StartsWith"] = "begynner med",
        ["MudDataGrid_True"] = "sann",
        ["MudDataGrid_Unsort"] = "Fjern sortering",
        ["MudDataGrid_Value"] = "Verdi",
        ["MudDataGridPager_AllItems"] = "Alle",
        ["MudDataGridPager_FirstPage"] = "Første side",
        ["MudDataGridPager_InfoFormat"] = "{0}–{1} av {2}",
        ["MudDataGridPager_LastPage"] = "Siste side",
        ["MudDataGridPager_NextPage"] = "Neste side",
        ["MudDataGridPager_PreviousPage"] = "Forrige side",
        ["MudDataGridPager_RowsPerPage"] = "Rader per side:",

        ["MudTable_Loading"] = "Laster …",
        ["MudTable_SelectAllRows"] = "Velg alle rader",
        ["MudTable_SelectRow"] = "Velg rad",
        ["MudTablePager_FirstPage"] = "Første side",
        ["MudTablePager_LastPage"] = "Siste side",
        ["MudTablePager_NextPage"] = "Neste side",
        ["MudTablePager_PreviousPage"] = "Forrige side",

        ["MudPagination_CurrentPage"] = "Gjeldende side {0}",
        ["MudPagination_PageIndex"] = "Side {0}",
        ["MudPagination_FirstPage"] = "Første side",
        ["MudPagination_LastPage"] = "Siste side",
        ["MudPagination_NextPage"] = "Neste side",
        ["MudPagination_PreviousPage"] = "Forrige side",

        ["Converter_InvalidNumber"] = "Ugyldig tall",
        ["Converter_InvalidDateTime"] = "Ugyldig dato",
        ["Converter_InvalidBoolean"] = "Ugyldig ja/nei-verdi"
    };

    public override LocalizedString this[string key] => Lookup(key);

    public override LocalizedString this[string key, params object[] arguments] => Lookup(key, arguments);

    private static LocalizedString Lookup(string key, params object[] arguments) =>
        Texts.TryGetValue(key, out var text)
            ? new LocalizedString(key, SafeFormat.Format(text, arguments))
            : new LocalizedString(key, key, resourceNotFound: true);
}
