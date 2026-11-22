# TranslationExtractor

Extrait les textes à traduire d'un mod RimWorld (defs XML **+ patchs** + Keyed) vers
`out\<packageId>\Languages\French\{DefInjected,Keyed}`. Chaque entrée est précédée de commentaires :

```xml
<!-- EN: Exosuit move speed -->
<!-- Contexte: StatDef : nom de statistique affiché à gauche de la valeur ... rester COURT -->
<!-- Limite: ~22 car. (EN: 18) max=22 -->
<ExosuitMoveSpeed.label>TODO</ExosuitMoveSpeed.label>
```

## Utilisation
```
extract.cmd aoba.exosuit.framework --scan-dll
extract.cmd "C:\...\workshop\content\294100\3352894993"
extract.cmd --list-mods exo
extract.cmd --check out\aoba.exosuit.framework\Languages\French
extract.cmd --calibrate Core        (compare à la traduction FR officielle)
```
Options : `--version 1.6`, `--with id1,id2` (mods optionnels actifs), `--out`, `--placeholder`, `--prefill-en`,
`--fresh` (sans fusion), `--strict` (champs [MustTranslate] seulement), `--scan-dll` (où sont utilisées les clés Keyed).
`--skip-shipped` (ne pas extraire les entrées que le mod traduit déjà lui-même), `--no-implicit`, `--respect-conditions`, `--dump-def <defName>` (affiche le XML fusionné).

## Comportement
- Relancer fusionne : les traductions déjà saisies sont conservées, les EN modifiés sont signalés
  (`ATTENTION`), les clés disparues passent en commentaire `OBSOLETE` (relus aux extractions suivantes, réutilisés si la clé revient). Un fichier que l'extraction ne produit plus est supprimé s'il ne contient que des `TODO`, sinon réécrit avec ses traductions en `OBSOLETE` (signalé dans `report.txt`).
- Textes modifiés par les patchs du mod : comparaison des couples (clé, texte) avant et après ses patchs (une même clé TKey peut figurer deux fois dans une quête avec deux textes).
- Textes ajoutés/modifiés par les patchs du mod sur d'autres mods/Core : fichiers `Patches_<fichier>.xml`.
- `context-rules.json` : règles de contexte/longueur éditables (premier match gagne). Une règle avec `"soft": true` (noms d'objets : `ThingDef.label`, `labelPlural` ; noms de sols : `TerrainDef.label`) produit une « Limite souple » : `--check` signale un dépassement en `LONG (souple)` sans le compter comme erreur.
  Emplacements de Sandy's RPG Style Inventory (`ItemSlotDef.label`, affichés en infobulle) : limite souple (21/11/2026).
- Texte anglais vide : l'entrée est écrite vide, pas `TODO` (rien à traduire) ; `Status-Translations.ps1` et `Sync-Translations.ps1` la comptent comme traduite.
- `--check` ne signale pas un `{X_possessive}` absent (le français s'en passe souvent, Conventions §6), ni un `{X_pronoun}` / `{X_objective}` remplacé par une forme `{X_gender ? … }` ou par le nom (`{X_nameDef}`, pour éviter un accord). La longueur compte une forme `{X_gender ? a : b}` pour sa variante la plus longue, pas pour sa syntaxe.
- Champ non marqué `[MustTranslate]` : un texte contenant `\n` n'est pas pris pour un chemin de fichier, et un nom de champ texte suffixé (`shortDesc_Direct`, `labelKey_X`) est reconnu (22/11/2026, Research Reinvented : 40 textes manqués).
  Les limites sont des repères : une traduction naturelle et fidèle au vocabulaire officiel du jeu peut dépasser de quelques caractères (`TROP LONG` accepté, ex. VRE Genie). Raccourcir seulement les vrais débordements.
- `report.txt` : comptes, patchs C# non exécutés, types introuvables, champs texte non marqués ignorés.
  Ces champs ignorés sont souvent des références (ex. `FactionDef.dialogFactionGreeting*` contient le defName d'un `RulePackDef`, traduit avec celui-ci) : vérifier dans le XML avant de parler de texte manquant.
- **Version et copies d'un même mod** : comme le jeu, `LoadFolders.xml` est lu pour la version visée, sinon pour la plus haute version déclarée inférieure, sinon `default`, sinon dossiers par défaut (racine, `Common`, meilleur dossier de version ≤ visée) ; un mod qui ne déclare que des versions plus récentes n'apporte donc que ce qui est à sa racine. Quand plusieurs mods ont le même packageId (ex. The Dead Man's Switch 1.6 et sa republication 1.7), l'outil prend la copie dont `supportedVersions` contient la version visée (`--version`), sinon la plus proche en dessous. `out\<packageId>` est commun aux deux : une extraction 1.7 fusionne avec les traductions déjà saisies.
- **Mods visés chargés automatiquement** : pour la passe « toutes conditions », l'outil charge aussi les mods installés que la cible vise (`IfModActive*` de son `LoadFolders.xml`, `MayRequire`/`MayRequireAnyOf`, noms de `PatchOperationFindMod`), sinon ses patchs sur ces mods ne trouvent rien (ex. Intimacy qui réécrit un gène de VRE Highmate). Seuls les textes que ses patchs ajoutent ou modifient chez eux sont gardés (fichiers `Patches_*.xml`) ; la liste est dans `report.txt`. `--with` reste possible pour un mod non référencé.
- **Toutes les conditions supposées remplies** (défaut) : deux passes, d'abord le mod seul (conditions respectées), puis avec tout actif (dossiers `IfModActive`, `MayRequire`, `FindMod`, CE…) ; l'union est extraite, la première passe l'emportant en cas de conflit (des variantes exclusives se remplacent l'une l'autre). `--respect-conditions` revient à une seule passe. Les patchs C# (ex. CE `MakeGunCECompatible`) restent non exécutés : leurs textes (outils `muzzle`/`head`, `Verb_ShootCE`…) ne peuvent pas être extraits.
- **Textes absents du XML** (désactivables par `--no-implicit`) : défauts fixés par le constructeur (`leaderTitle = "leader"`, lus dans l'IL, champs `[MustTranslate]` seulement) et formes déduites d'un autre champ, listées dans `implicit-rules.json` (`titleFemale`, `labelPlural`/`labelMale`/`labelFemale` des PawnKindDef et de leurs `lifeStages`, `labelNoun`, `stuffAdjective`, libellé du verbe unique d'une arme = nom de l'objet…). Ces entrées portent « Texte absent du XML : … » ; laissées à `TODO`, elles ne sont pas copiées dans un pack.
- Héritage XML : les `<li>` d'un enfant s'ajoutent à ceux du parent (comme `XmlInheritance`), sauf `Inherit="False"`.
- Listes de textes (`List<string>`) : une liste entière en `<li>` n'est acceptée par le jeu que si le champ porte `[TranslationCanChangeCount]` (`rulesStrings`…). Sinon (`GeneDef.customEffectDescriptions`, `traitAdjectives`, `namerLabels`, `tips` de certains mods…), le jeu rejette toute la liste (« doesn't have [TranslationCanChangeCount] attribute » dans le rapport de traduction) : l'outil écrit une clé par élément (`<X.customEffectDescriptions.0>`), comme la traduction officielle (corrigé le 21/11/2026 d'après le rapport de traduction du jeu).
- Handle d'un élément de `comps` sans `compClass` dans le XML : classe fixée par le constructeur des `CompProperties` (`CompProperties_Launchable` → `CompLaunchable`), lue dans l'IL sur la séquence exacte `ldtoken` / `stfld compClass` (avant le 21/11/2026, une lecture trop large donnait `ThingComp`, clé rejetée par le jeu).
- Textes de quêtes (`SlateRef<string>`, ex. `<label TKey="LetterLabelShuttleArrived">`) : clé `<defName>.<TKey>.slateRef` comme la traduction officielle, sinon `<chemin>.slateRef` ; les références de variables (`$asker`) sont ignorées. Les objets polymorphes (`<root Class="QuestNode_Sequence">`, `genStep`, `node`…) sont parcourus selon leur classe réelle.
- Textes que le mod ne donne que dans `Languages/English*/DefInjected` (absents du XML de ses defs, ex. Simple Improve) : ajoutés avec leur texte anglais (sauf clé indexée en doublon d'un champ déjà extrait).
- `BackstoryDef` : le XML donne `baseDesc`, mais `PostLoad` le recopie dans `description` avant l'injection des traductions ; la clé extraite est donc `description` (comme la traduction officielle), une clé `baseDesc` ne serait jamais affichée.
- Dossier `DefInjected` = type de def qui possède une base de données dans le jeu (ancêtre non abstrait le plus haut sous `Verse.Def`), comme `GenDefDatabase` : `Vehicles.VehicleDef`, `CombatExtended.AmmoDef`, `AlienRace.ThingDef_AlienRace` → `ThingDef` ; `GenelineGeneDef` → `GeneDef`. Un dossier au nom d'une sous-classe est chargé par le jeu mais ne trouve aucune def : ses traductions sont perdues. Relancer l'extraction (sans `--fresh`) déplace les anciennes traductions vers le bon dossier et supprime l'ancien fichier (signalé dans `report.txt`).

- **Fichiers de mots** (ajout du 21/11/2026) : les générateurs de texte (noms, titres de livres…) tirent des mots dans `Languages/<langue>/Strings/<dossier>/**/*.txt`, désignés par `RulePack.rulesFiles` (`symbole->Words/BookWords/BookStyles`, chemin sans `.txt`). Le jeu cherche le fichier dans la langue active au même chemin, sinon en anglais (`Translator.TryGetTranslatedStringsForFile`) ; il ignore le texte après `//` et les lignes vides (`GenText.LinesFromString`) ; il ne lit que les sous-dossiers de `Strings`.
  - Chaque `.txt` anglais du mod est écrit dans `out\<mod>\Languages\French\Strings\<même chemin>` : en-tête `//` (RulePackDef qui l'utilise, consignes), puis une ligne `TODO // EN: mot` par entrée (doublons gardés : ils pèsent dans le tirage). Remplacer `TODO` par le français ; garder `// EN: …`.
  - Relancer reprend les traductions par texte anglais. Un fichier où une ligne n'a pas de `// EN:` est considéré comme restructuré à la main et n'est plus réécrit (signalé dans `report.txt`).
  - `RulePack.rulesFiles` est extrait comme liste entière (`[TranslationCanChangeCount]`), **pré-rempli avec l'anglais** : rien à faire tant que les listes gardent le même chemin. Pour accorder (féminin, élision, pluriel), découper la liste en plusieurs fichiers et modifier `rulesFiles` et `rulesStrings`, comme l'officiel (`GlobalUtility` : `BusinessType_Feminine->Words/Nouns/BusinessTypes_Feminine`). Une liste pré-remplie non modifiée suit l'anglais si le mod change ses chemins.
  - `--check` compte aussi les lignes `TODO` des `.txt`. La synchro du pack ne copie un `.txt` que s'il ne reste aucun `TODO` (sinon la liste anglaise reste utilisée) : un fichier de noms inventés peut donc rester à `TODO` (Conventions §1).
- Si le mod embarque déjà du français (`Languages\French*`), l'outil l'annonce en console et dans `report.txt` (section « traduction déjà fournie par le mod ») : couverture, entrées manquantes, clés obsolètes, anciennes clés indexées (`comps.0.tools.0`). la traduction du mod n'est jamais modifiée.

## limites connues
- Patchs `PatchOperation` en C# (classe personnalisée) non exécutés : listés dans le rapport.
- Textes générés par le code du jeu non extraits : recettes/terrains implicites (Make_*, roche brute),
  valeurs par défaut calculées dans du code (non littérales), `debugLabel`. Le jeu génère ces recettes avec le Keyed de la langue.
- Contexte d'affichage heuristique (règles + analyse des chaînes des DLL).
- defName commençant par un chiffre (ex. `2S38_Tank_Turret`) : la clé est écrite comme nom de balise
  (`<2S38_Tank_Turret.label>`), ce qui n'est pas du XML valide ; le jeu rejette alors **tout le fichier**.
  Le jeu accepte la syntaxe `<rep><path>2S38_Tank_Turret.label</path><trans>…</trans></rep>`
  (`DefInjectionPackage.AddDataFromFile`), que l'outil n'écrit pas et que `--fill-tm` ne lit pas.
  Aucun cas dans `out\` au 21/11/2026 (seuls des mods inactifs ou des defs sans texte sont concernés :
  Save Our Ship 2, Empire de matathias, `2x2CargoHoldGraphics`). À traiter si un mod traduit en a un.
- `Rule_File` placés dans `rulesRaw` (`path`, `pathList`, champs `[MayTranslate]`) : non extraits ; le fichier `.txt` visé l'est quand même s'il est fourni par le mod.
- Fichiers de mots du jeu de base (`Words/Nouns/Animals`…) utilisés par un mod : traduits par l'officiel, rien à faire.

## Repetitive translation helpers (post-extraction)

These commands only read and write the language files under `out\` (or a folder given as path); they never touch the game, the workshop or a mod. They fill **only** entries still at `TODO`, keep every comment, and put an `<!-- Auto (...) : à relire -->` comment above each value they write, so generated text can be reviewed (re-running the extraction drops that comment but keeps the value).

### Translation memory: `--fill-tm`
```
extract.cmd --fill-tm ks.flooringfloors            (same mod only)
extract.cmd --fill-tm lucius.floorpatch --tm-global (then every mod under out\)
```
`extract.cmd --fill-tm all` does the same for every mod under `out\` in one run (one memory built from all of them before filling).
For each `TODO`, copies the translation of another entry whose `EN:` comment is exactly the same text: first from the same mod, then (with `--tm-global`) from any mod under `out\`. Entries flagged `ATTENTION` (English changed since translation) are not used as a source. When the same English has several translations, the most frequent one is taken and the case is listed ("à harmoniser"). The report gives the number of filled entries and what is left: remaining `TODO`s, number of **distinct** English texts, and the most repeated ones. Workflow: translate one occurrence of each text, then run `--fill-tm` again. List entries (`<li>`) are not handled.

### Label generator for "material x pattern" mods: `--label-tables`, `--fill-labels`
Made for Flooring Floors and its patch mods, where labels combine a material and a pattern ("sapphire crosshatch M tile", "V cocoawood strip floor").
```
extract.cmd --label-tables ks.flooringfloors,lucius.floorpatch
   (edit out\ks.flooringfloors\label-patterns.csv and label-materials.csv)
extract.cmd --fill-labels ks.flooringfloors,lucius.floorpatch
extract.cmd --fill-labels ks.flooringfloors,lucius.floorpatch --relabel   (after correcting the tables)
```
- `--label-tables` splits every `.label` of the `DefInjected\TerrainDef` files (option `--label-folder`) into a pattern template and a material, then writes or **updates** two tables (default: in `out\<first id>\`, option `--tables <folder>`). Existing French values are kept, counts are refreshed.
  - `label-patterns.csv`: `en;fr;count;example`, e.g. `{M} crosshatch tile;carrelage en croisillon de {M};94;…`
  - `label-materials.csv`: `en;fr;elision;count;example;suggestion`. `suggestion` = French already given to the same English label somewhere under `out\` (our translations of the source mods), to copy into `fr` after checking.
  - CSV: `;` separator, UTF-8 with BOM (opens in Excel), `"..."` quoting.
- The split is inferred from the labels, not from defNames (`KS_StoneGravelKObsidian` is "schorl gravel paving"): each 1-3 word span is tried as material; the split whose template is shared by the most materials and whose material appears in the most templates wins, then a few rounds re-split each label after the majority (irregular families such as "stainless steel arx tile"). Splits used by fewer than `--min-share` (3) labels are dropped: those labels (typos, labels with no material) stay for the TM or manual translation.
- `--fill-labels` writes `fr` of the pattern with `{M}` replaced by `fr` of the material, for `TODO` labels whose pattern **and** material are translated. Write the pattern so that no agreement is needed ("carrelage en croisillon de {M}" → "carrelage en croisillon de saphir"). `de` + vowel becomes `d'` ("d'émeraude"); column `elision` forces it (`oui`, e.g. h muet: "d'hématite") or prevents it (`non`). `--relabel` also regenerates labels still carrying the generator's review comment.
- The report lists the untranslated patterns and materials that block the most labels.
- Then check with `--check`. `TerrainDef.label` has a soft limit (like `ThingDef.label`): the French "… de <material>" form is usually longer than the English, so overruns show as `LONG (souple)`, not as errors.

### Translation worksheets: `--export-todo`, `--import`
```
extract.cmd --export-todo adamas.vendingmachines,zal.titlesforme [--to <file>] [--batch 300]
   (fill the text after the last "||" on each line)
extract.cmd --import feuilles\todo_adamas.vendingmachines+1_20261121-233000.txt
```
- `--export-todo` writes every entry still at `TODO` (`all` = every mod under `out\`) to a plain text file, by default `feuilles\todo_<id>_<date>.txt` (never overwrites an existing sheet; `--batch N` splits it into files of N entries). Each entry is one line `packageId || file under Languages\French || key || ` with, above it, the English text and its context (context, length limit, placeholders to keep, official French, "texte anglais modifié").
- A list entry has the key `key[]`; its items, in English and in French, are separated by ` ¶ `. The number of items may change for rule lists (`rulesStrings`).
- The translation is plain text: `<color=…>`, `&` and `\n` are written as such, `--import` escapes them for XML. Empty lines are skipped (translate in several passes).
- `--import` only replaces entries **still at `TODO`** (an entry translated since the export is reported, not overwritten), checks that each file still loads as XML before writing it, and lists the mods touched. Then: `Fix-Typography.ps1 -Path out\<mod>`, `--check`, and the pack sync of those mods.

### Comparison with a third-party translation: `--compare-ref`
```
extract.cmd --compare-ref bodlosh.weaponstats --ref 2798150558
```
- `--ref`: the translation mod (packageId, Workshop ID, name) or a folder. Every `Languages\French*` folder of it is read (the game version's folder wins when a key is in several). Read-only; writes `out\<id>\compare-ref.csv` (`etat;type;cle;anglais;nous;reference`).
- Keys translated on both sides: identical or different, typography left out (quotes, spaces before punctuation, case). **Short labels translated differently** (English up to 40 characters) are printed: that is where missed contexts show (Nov. 2026: *sway* read as recoil, *belt* not translated as the official « utilitaire »).
- Keys only in the reference, looked up in the target mod (it must be installed): the def exists in the current version and has that field (own XML or abstract parents), or the exact key is in its English DefInjected (`extraction ?`: a text our extraction may have missed, to check); the def exists without that field (`champ absent du mod actuel`: an old key); only in other files of the mod (`ancienne version`), or nowhere (`absente du mod`: another mod covered by the same translation pack, or a removed key).
