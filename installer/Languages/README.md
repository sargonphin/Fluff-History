# Pinned Inno language sources

These base translations come from Inno Setup **7.1.0**, upstream commit
`7d634620f1d1caca6c76e8398da7133153db9e7b` (tag `is-7_1_0`). Files marked
`distribution` in `../LanguageSources.json` were copied from the installed pinned
compiler and their original Git blob hashes checked against that upstream tag.
Other files come from the same upstream tag's contributed-language directory.

Original translation credits remain in each file. `InnoSetup-LICENSE.txt` preserves
the upstream distribution terms. The snapshots have only their line endings and
UTF-8 BOM normalized for compilation; `textSha256` hashes normalized text without
the BOM. Canonical builds use these checked-in files and never fetch translations.

The source inventory covers 43 target languages. The pinned upstream tree contains
no Filipino/Tagalog base translation; that explicit installer exception does not
remove `fil-PH` from Desktop or native Shell requirements. Bengali's upstream base
uses the Bengali (India) ID; Serbian bases use older Serbian IDs. Matching those
to the target Windows locale IDs remains part of final installer generation.

Some older contributed bases need compatibility work before they can be advertised
as complete Inno 7 translations: Romanian and Latvian lack 23 current message keys;
Estonian, Hindi and Bengali lack 67; Malay lacks 83. Several obsolete directives or
message names also produce compiler warnings. Empty `HelpTextNote` omissions in
otherwise current bases have no visible untranslated text. Do not count a successful
compiler run with English-fallback warnings as full translation validation.

The current catalogs contain all 44 Desktop/Shell languages; generated product
messages cover the 43 Setup bases listed below. The Filipino base exception and
standard-message English fallback above are explicit limitations, not missing
application catalogs. Source/generation validation and successful Inno compilation
do not establish a separate installed visual campaign for every language.
See [localization](../../docs/LOCALIZATION.md) for current behavior and validation.
## Included installer languages

| Language | Target tag |
| --- | --- |
| English (United States) | en-US |
| German (Germany) | de-DE |
| French (France) | fr-FR |
| Spanish (Spain, International Sort) | es-ES |
| Italian (Italy) | it-IT |
| Portuguese (Brazil) | pt-BR |
| Portuguese (Portugal) | pt-PT |
| Dutch (Netherlands) | nl-NL |
| Polish (Poland) | pl-PL |
| Czech (Czechia) | cs-CZ |
| Slovak (Slovakia) | sk-SK |
| Hungarian (Hungary) | hu-HU |
| Romanian (Romania) | ro-RO |
| Bulgarian (Bulgaria) | bg-BG |
| Croatian (Croatia) | hr-HR |
| Slovenian (Slovenia) | sl-SI |
| Serbian (Latin, Serbia) | sr-Latn-RS |
| Serbian (Cyrillic, Serbia) | sr-Cyrl-RS |
| Greek (Greece) | el-GR |
| Turkish (Türkiye) | tr-TR |
| Russian (Russia) | ru-RU |
| Ukrainian (Ukraine) | uk-UA |
| Swedish (Sweden) | sv-SE |
| Norwegian Bokmål (Norway) | nb-NO |
| Danish (Denmark) | da-DK |
| Finnish (Finland) | fi-FI |
| Estonian (Estonia) | et-EE |
| Latvian (Latvia) | lv-LV |
| Lithuanian (Lithuania) | lt-LT |
| Catalan (Catalan) | ca-ES |
| Arabic (Saudi Arabia) | ar-SA |
| Hebrew (Israel) | he-IL |
| Persian (Iran) | fa-IR |
| Hindi (India) | hi-IN |
| Bangla (Bangladesh) | bn-BD |
| Chinese (Simplified) | zh-Hans |
| Chinese (Traditional) | zh-Hant |
| Japanese (Japan) | ja-JP |
| Korean (Korea) | ko-KR |
| Thai (Thailand) | th-TH |
| Vietnamese (Vietnam) | vi-VN |
| Indonesian (Indonesia) | id-ID |
| Malay (Malaysia) | ms-MY |

Filipino (fil-PH) is included in Desktop and native Shell, but has no selectable
Setup base in the pinned upstream inventory.

Sources: [pinned upstream distribution](https://github.com/jrsoftware/issrc/tree/7d634620f1d1caca6c76e8398da7133153db9e7b/Files/Languages),
[contributed translations](https://github.com/jrsoftware/issrc/tree/7d634620f1d1caca6c76e8398da7133153db9e7b/Files/Languages/Unofficial),
[Inno language selection](https://jrsoftware.org/ishelp/topic_languagessection.htm),
[custom messages](https://jrsoftware.org/ishelp/topic_custommessagessection.htm).
