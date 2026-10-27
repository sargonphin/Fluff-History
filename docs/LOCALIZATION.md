# Localization

Desktop and native Shell resources cover 44 language catalogs. The canonical source
is `src/FluffHistory.Desktop/Strings/en-US/Resources.resw`; translations use the same
keys and English-first changes. The catalogs contain 674 keys, 541 imperative
identifiers and 133 live XAML properties. Those are source counts, not
claims of individual native-speaker or installed visual review for every language.

## Selection and formatting

Application Settings offers Use Windows language plus exact supported tags. The
optional app-local `language` preference shares `preferences-v1.json` with appearance
and Timeline view; absent means Windows matching. Invalid input uses safe defaults
without rewriting it. Changing language prepares an MRT context, saves atomically,
then notifies existing controls/view models. No application restart is required.

XAML uses attached `Localize.Uid`; imperative text uses `IAppStrings` and generated
`TextId`. Generated dependency-property bindings include accessibility/tooltips and
use a bounded key cache without retaining pages. Missing translated candidates use
MRT fallback; missing canonical keys are defects, not blank labels. Per-context
selection does not set process/package PrimaryLanguageOverride or affect brokers.

Existing configuration drafts, Timeline folder/time/capture and exact selections
survive switching. Reading direction follows resolved UI language, while regional
CurrentCulture independently formats dates, numbers and decimal sizes. Paths and
identifiers in RTL prose use bidi isolation. New modal dialogs use the language at
opening; language switching is unavailable behind the modal.

Diagnostics/About technical data stays English. Repository paths/identity, enum
serialization, IPC, URLs, crypto domains, algorithms and release versions are never
translated. Windows-owned Shell/installer/control captions follow their own resource
selection; framework ToggleSwitch On/Off captions can still follow Windows language.

## Shipping tags

`en-US`, `de-DE`, `fr-FR`, `es-ES`, `it-IT`, `pt-BR`, `pt-PT`, `nl-NL`, `pl-PL`,
`cs-CZ`, `sk-SK`, `hu-HU`, `ro-RO`, `bg-BG`, `hr-HR`, `sl-SI`, `sr-Latn-RS`,
`sr-Cyrl-RS`, `el-GR`, `tr-TR`, `ru-RU`, `uk-UA`, `sv-SE`, `nb-NO`, `da-DK`,
`fi-FI`, `et-EE`, `lv-LV`, `lt-LT`, `ca-ES`, `ar-SA`, `he-IL`, `fa-IR`, `hi-IN`,
`bn-BD`, `zh-Hans`, `zh-Hant`, `ja-JP`, `ko-KR`, `th-TH`, `vi-VN`, `id-ID`,
`ms-MY`, `fil-PH`.

`ApplicationLanguage` defines the exact choice set. Chinese script tags are
intentional. Native Shell resources are generated from shared `Shell_*` keys with
stable IDs and RTL dialog styles. Open4 carries the native page's captured resource
language to its broker; it remains separate from management preference.

## Installer languages

`installer/LanguageSources.json` pins offline Inno 7.1.0 base files and normalized
hashes. `Installer_*` product messages generate 43 selectable Setup languages from
the shared catalogs. Filipino lacks a pinned upstream base and is the explicit
Setup-only exception; Desktop and Shell still include it.

Standard-message compatibility/fallback remains for older bases: Romanian/Latvian
lack 23 keys, Estonian/Hindi/Bengali 67, and Malay 83. Some old directives produce
warnings. Bengali/Serbian base IDs are mapped to the target Windows locales. Product
custom messages are translated, but a successful compiler exit with base-message
English fallback does not prove a fully translated installer. Exact upstream
translator credits and license files remain unchanged.

## Editing and validation

Edit canonical English and translations, then use `scripts/Test-Localization.ps1`.
It runs literal/raw-exception guards, negative guard fixtures, exact catalog and
placeholder/whitespace/brand checks, and `--check` generation for typed identifiers,
live bindings, native resources, installer messages and manifests. Do not edit
generated output as an alternative source of truth. Explicit identical translations
require justified entries in the tool's allowlist.

Release-branding validation fixes the normal About heading to the untranslated
product name `Fluff-History`, rejects preview-specific About identifiers and checks
About resource text for Developer Preview branding. Separate development build/install
branding remains permitted. The product-name equality exception is explicit per locale.

`tools/FluffHistory.Localization.Runtime` and
`scripts/Test-LocalizationRuntime.ps1` check actual PRI resolution/fallback. Use
representative Latin, RTL and CJK visual checks after structural presentation changes;
batch catalog validation is not a native-speaker translation review. See
[testing](TESTING.md) and [Shell integration](SHELL-INTEGRATION.md).
