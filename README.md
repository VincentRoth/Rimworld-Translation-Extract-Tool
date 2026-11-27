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

## Réextraction et fusion

- Relancer fusionne : les traductions déjà saisies sont conservées, les EN modifiés sont signalés
  (`ATTENTION`), les clés disparues passent en commentaire `OBSOLETE` (relus aux extractions suivantes, réutilisés si la clé revient). Un fichier que l'extraction ne produit plus est supprimé s'il ne contient que des `TODO`, sinon réécrit avec ses traductions en `OBSOLETE` (signalé dans `report.txt`).
- Clé renommée (24/11/2026) : une clé nouvelle reprend la traduction d'une clé disparue du même fichier au même texte anglais (si toutes ces clés ont la même traduction), avec un commentaire `Auto (clé renommée par le mod, reprise de …) : à relire` (ex. Inclusive Skin Tones, `IST.` → `InclusiveSkinTones.`). Les `OBSOLETE` gardent désormais leur texte `EN:` pour cela. Une liste dont l'anglais a changé reçoit aussi `ATTENTION` ; un texte contenant `--` n'est plus signalé à tort.
- Texte anglais vide : l'entrée est écrite vide, pas `TODO` (rien à traduire) ; `Status-Translations.ps1` et `Sync-Translations.ps1` la comptent comme traduite.
- Si le mod embarque déjà du français (`Languages\French*`), l'outil l'annonce en console et dans `report.txt` (section « traduction déjà fournie par le mod ») : couverture, entrées manquantes, clés obsolètes, anciennes clés indexées (`comps.0.tools.0`). La traduction du mod n'est jamais modifiée.

## Ce qui est extrait

### Version, dossiers et conditions
- **Version et copies d'un même mod** : comme le jeu, `LoadFolders.xml` est lu pour la version visée, sinon pour la plus haute version déclarée inférieure, sinon `default`, sinon dossiers par défaut (racine, `Common`, meilleur dossier de version ≤ visée) ; un mod qui ne déclare que des versions plus récentes n'apporte donc que ce qui est à sa racine. Quand plusieurs mods ont le même packageId (ex. The Dead Man's Switch 1.6 et sa republication 1.7), l'outil prend la copie dont `supportedVersions` contient la version visée (`--version`), sinon la plus proche en dessous. `out\<packageId>` est commun aux deux : une extraction 1.7 fusionne avec les traductions déjà saisies.
- **Toutes les conditions supposées remplies** (défaut) : deux passes, d'abord le mod seul (conditions respectées), puis avec tout actif (dossiers `IfModActive`, `MayRequire`, `FindMod`, CE…) ; l'union est extraite, la première passe l'emportant en cas de conflit (des variantes exclusives se remplacent l'une l'autre). `--respect-conditions` revient à une seule passe. Les patchs C# (ex. CE `MakeGunCECompatible`) restent non exécutés : leurs textes (outils `muzzle`/`head`, `Verb_ShootCE`…) ne peuvent pas être extraits.
- Deux passes : une clé `x-0` de la seconde passe qui est le `x` de la première (un élément de plus au même nom) n'est plus ajoutée (« Duplicate def-injected translation key »).
- **Mods visés chargés automatiquement** : pour la passe « toutes conditions », l'outil charge aussi les mods installés que la cible vise (`IfModActive*` de son `LoadFolders.xml`, `MayRequire`/`MayRequireAnyOf`, noms de `PatchOperationFindMod`), sinon ses patchs sur ces mods ne trouvent rien (ex. Intimacy qui réécrit un gène de VRE Highmate). Seuls les textes que ses patchs ajoutent ou modifient chez eux sont gardés (fichiers `Patches_*.xml`) ; la liste est dans `report.txt`. `--with` reste possible pour un mod non référencé.

### Patchs
- Textes ajoutés/modifiés par les patchs du mod sur d'autres mods/Core : fichiers `Patches_<fichier>.xml`.
- Textes modifiés par les patchs du mod : comparaison des couples (clé, texte) avant et après ses patchs (une même clé TKey peut figurer deux fois dans une quête avec deux textes).
- Patchs dépendant d'une option (24/11/2026) : `XmlExtensions.OptionalPatch` / `FindMod` / `UseSetting` / `PatchOperationSafeAdd`, et toute classe qui enveloppe des opérations (`operations`, `patch`, `match`, `apply`, `caseTrue` : `PatchOperationModOption`, `PatchOperationIfToggled`, `PatchOperationFeature`…). La branche par défaut est exécutée comme un patch ordinaire ; une passe de plus exécute toutes les branches et n'ajoute que des clés **nouvelles** (un texte existant modifié par une option garderait sa traduction quand l'option est désactivée).

### Champs et textes
- Héritage XML : les `<li>` d'un enfant s'ajoutent à ceux du parent (comme `XmlInheritance`), sauf `Inherit="False"`.
- Champ non marqué `[MustTranslate]` : un texte contenant `\n` n'est pas pris pour un chemin de fichier, et un nom de champ texte suffixé (`shortDesc_Direct`, `labelKey_X`) est reconnu (22/11/2026, Research Reinvented : 40 textes manqués).
- Champs `[Unsaved]` : non extraits (refusés par le jeu, comme `[NoTranslate]`).
- **Champ non marqué servant d'identifiant** (27/11/2026) : non extrait quand le code compare sa valeur à une donnée non traduite ou à un littéral, s'en sert comme clé de dictionnaire, defName (`GetNamed`), chemin de texture, nom de type ou packageId ; aussi quand sa valeur d'un mot est passée à `Translate()` (clé Keyed) ou que son nom finit par `Key` (`labelKey = PackAnimal`). Cas d'origine : Facial Animation `FaceAdjustmentDef.RightEyeLabel` (« right eye ») comparé à `BodyPartRecord.untranslatedCustomLabel` ; traduit, les visages des colons ne se chargeaient plus (`Sequence contains no elements` dans `EyeballControllerComp.LoadTextures`). Autres cas réels : VEF `TextureVariantOverride.groupName` ↔ `GraphicPart.name`, VPE Mechanitor Sentinel `ModExt_BuffPawnKind.bodyPartLabel`.
  - Analyse de l'IL des DLL chargées (`FieldUsage.cs`, sans exécution, environ 2 s pour tout le workshop) : la valeur est suivie à travers une variable locale, un champ de closure (lambda) et une petite méthode qui ne fait que renvoyer le champ (`GetRightEyeLabel()`). Une comparaison prend les deux derniers opérandes chargés.
  - Restent extraits : un champ comparé à un champ `[MustTranslate]` (les deux sont traduits pareil, ex. MVCF `AdditionalVerbProps.label` ↔ `VerbProperties.label`), un champ comparé à lui-même (doublons), les comparaisons dans `ConfigErrors`, `GroupsWith`, `MergeWith`, et `x == ""` / `x == null`.
  - `report.txt`, section « Champs ignorés : utilisés comme identifiant par le code » (champ, raison, méthode, exemple) : défs du mod seulement ; une exclusion sur la def d'un autre mod modifiée par un patch n'y figure pas. Une traduction déjà saisie pour une clé exclue passe en commentaire `OBSOLETE` à la réextraction.
  - Les textes de `Languages/English*/DefInjected` du mod et les textes de quête (`slateRef`) ne passent pas par ce filtre.
- **Textes absents du XML** (désactivables par `--no-implicit`) : défauts fixés par le constructeur (`leaderTitle = "leader"`, lus dans l'IL, champs `[MustTranslate]` seulement) et formes déduites d'un autre champ, listées dans `implicit-rules.json` (`titleFemale`, `labelPlural`/`labelMale`/`labelFemale` des PawnKindDef et de leurs `lifeStages`, `labelNoun`, `stuffAdjective`, libellé du verbe unique d'une arme = nom de l'objet…). Ces entrées portent « Texte absent du XML : … » ; laissées à `TODO`, elles ne sont pas copiées dans un pack.
- Textes que le mod ne donne que dans `Languages/English*/DefInjected` (absents du XML de ses defs, ex. Simple Improve) : ajoutés avec leur texte anglais (sauf clé indexée en doublon d'un champ déjà extrait).
- `BackstoryDef` : le XML donne `baseDesc`, mais `PostLoad` le recopie dans `description` avant l'injection des traductions ; la clé extraite est donc `description` (comme la traduction officielle), une clé `baseDesc` ne serait jamais affichée.

### Fichiers de mots
- **Fichiers de mots** (ajout du 21/11/2026) : les générateurs de texte (noms, titres de livres…) tirent des mots dans `Languages/<langue>/Strings/<dossier>/**/*.txt`, désignés par `RulePack.rulesFiles` (`symbole->Words/BookWords/BookStyles`, chemin sans `.txt`). Le jeu cherche le fichier dans la langue active au même chemin, sinon en anglais (`Translator.TryGetTranslatedStringsForFile`) ; il ignore le texte après `//` et les lignes vides (`GenText.LinesFromString`) ; il ne lit que les sous-dossiers de `Strings`.
  - Chaque `.txt` anglais du mod est écrit dans `out\<mod>\Languages\French\Strings\<même chemin>` : en-tête `//` (RulePackDef qui l'utilise, consignes), puis une ligne `TODO // EN: mot` par entrée (doublons gardés : ils pèsent dans le tirage). Remplacer `TODO` par le français ; garder `// EN: …`.
  - Relancer reprend les traductions par texte anglais. Un fichier où une ligne n'a pas de `// EN:` est considéré comme restructuré à la main et n'est plus réécrit (signalé dans `report.txt`).
  - `RulePack.rulesFiles` est extrait comme liste entière (`[TranslationCanChangeCount]`), **pré-rempli avec l'anglais** : rien à faire tant que les listes gardent le même chemin. Pour accorder (féminin, élision, pluriel), découper la liste en plusieurs fichiers et modifier `rulesFiles` et `rulesStrings`, comme l'officiel (`GlobalUtility` : `BusinessType_Feminine->Words/Nouns/BusinessTypes_Feminine`). Une liste pré-remplie non modifiée suit l'anglais si le mod change ses chemins.
  - `--check` compte aussi les lignes `TODO` des `.txt`. La synchro du pack ne copie un `.txt` que s'il ne reste aucun `TODO` (sinon la liste anglaise reste utilisée) : un fichier de noms inventés peut donc rester à `TODO` (Conventions §1).
  - `Rule_File` placés dans `rulesRaw` : `path` et `pathList` sont extraits, pré-remplis avec l'anglais comme `rulesFiles` (24/11/2026).
  - Fichiers de mots du jeu de base (`Words/Nouns/Animals`…) utilisés par un mod : traduits par l'officiel, rien à faire.

## Forme des clés

- Dossier `DefInjected` = type de def qui possède une base de données dans le jeu (ancêtre non abstrait le plus haut sous `Verse.Def`), comme `GenDefDatabase` : `Vehicles.VehicleDef`, `CombatExtended.AmmoDef`, `AlienRace.ThingDef_AlienRace` → `ThingDef` ; `GenelineGeneDef` → `GeneDef`. Un dossier au nom d'une sous-classe est chargé par le jeu mais ne trouve aucune def : ses traductions sont perdues. Relancer l'extraction (sans `--fresh`) déplace les anciennes traductions vers le bon dossier et supprime l'ancien fichier (signalé dans `report.txt`).
- Listes de textes (`List<string>`) : une liste entière en `<li>` n'est acceptée par le jeu que si le champ porte `[TranslationCanChangeCount]` (`rulesStrings`…). Sinon (`GeneDef.customEffectDescriptions`, `traitAdjectives`, `namerLabels`, `tips` de certains mods…), le jeu rejette toute la liste (« doesn't have [TranslationCanChangeCount] attribute » dans le rapport de traduction) : l'outil écrit une clé par élément (`<X.customEffectDescriptions.0>`), comme la traduction officielle (corrigé le 21/11/2026 d'après le rapport de traduction du jeu).
- Handles des éléments de liste (24/11/2026) : calculés comme `TranslationHandleUtility`. Nœuds de quête par leur type (`nodes.Letter`, `nodes.WorldObjectTimeout`) ; libellé tout en chiffres → `_1` (`stages.1` désignait le **rang** 1, donc le mauvais stade, ex. Lucy's Implants) ; libellé sans caractère latin (chinois) → rang (avant : `x`, refusé par le jeu) ; numéro `-N` compté comme le jeu.
- Handle d'un élément de `comps` sans `compClass` dans le XML : classe fixée par le constructeur des `CompProperties` (`CompProperties_Launchable` → `CompLaunchable`), lue dans l'IL sur la séquence exacte `ldtoken` / `stfld compClass` (avant le 21/11/2026, une lecture trop large donnait `ThingComp`, clé rejetée par le jeu).
- Textes de quêtes (`SlateRef<string>`, ex. `<label TKey="LetterLabelShuttleArrived">`) : clé `<defName>.<TKey>.slateRef` seulement pour les TKey **connus du jeu** (Core et DLC), sinon `<chemin>.slateRef` : le jeu utilise une table codée en dur (`TKeySystemHardcodedMapping`, lue dans l'IL) et ignore les attributs `TKey` des mods (24/11/2026, « Field or TKey … does not exist » des quêtes « Ancient … »). Les traductions saisies sous l'ancienne clé sont reprises à la réextraction ; les références de variables (`$asker`) sont ignorées. Les objets polymorphes (`<root Class="QuestNode_Sequence">`, `genStep`, `node`…) sont parcourus selon leur classe réelle.
- Clé qui n'est pas un nom de balise valide (defName commençant par un chiffre) : écrite `<rep><path>2S38.label</path><trans>…</trans></rep>` (24/11/2026), une clé par élément pour les listes ; lue par `--fill-tm`, `--check`, `--export-todo`/`--import`, `--compare-ref`, la synchro et les scripts du pack.
- defName contenant un point : le jeu coupe la clé au premier point et ne trouve pas la def, aucune clé DefInjected ne marche. Menus XML Extensions (`SettingsMenuDef`, ex. Some More Jobs) : textes extraits en Keyed (`Keyed\DFP_XmlExtensionsMenu.xml`, une clé par texte distinct) et patch `out\<mod>\Patches\DFP_XmlExtensionsMenu_tKeys.xml`, qui ajoute `tKey` / `tKeyTip` / `tKeyMessage` aux éléments repérés par leur texte anglais (sans effet si le texte change ; rien ne change dans les autres langues) ; la synchro le copie dans `T\<code>\Patches\`. Les traductions déjà saisies sont reprises. Valeurs de réglage (`value`, `defaultValue`) et autres types : non extraits, comptés dans `report.txt`.

## Contexte et limites de longueur

- `context-rules.json` : règles de contexte/longueur éditables (premier match gagne). Une règle avec `"soft": true` (noms d'objets : `ThingDef.label`, `labelPlural` ; noms de sols : `TerrainDef.label`) produit une « Limite souple » : `--check` signale un dépassement en `LONG (souple)` sans le compter comme erreur.
  Emplacements de Sandy's RPG Style Inventory (`ItemSlotDef.label`, affichés en infobulle) : limite souple (21/11/2026).
- Les limites sont des repères : une traduction naturelle et fidèle au vocabulaire officiel du jeu peut dépasser de quelques caractères (`TROP LONG` accepté, ex. VRE Genie). Raccourcir seulement les vrais débordements.
- Contexte d'affichage heuristique (règles + analyse des chaînes des DLL).

## Contrôle

- `--check` ne signale pas un `{X_possessive}` absent (le français s'en passe souvent, Conventions §6), ni un `{X_pronoun}` / `{X_objective}` remplacé par une forme `{X_gender ? … }` ou par le nom (`{X_nameDef}`, pour éviter un accord). La longueur compte une forme `{X_gender ? a : b}` pour sa variante la plus longue, pas pour sa syntaxe.
- `report.txt` : comptes, patchs C# non exécutés, types introuvables, champs texte non marqués ignorés.
  Ces champs ignorés sont souvent des références (ex. `FactionDef.dialogFactionGreeting*` contient le defName d'un `RulePackDef`, traduit avec celui-ci) : vérifier dans le XML avant de parler de texte manquant.
- `--calibrate <Core|Royalty|…>` : compare l'extraction à la traduction officielle ; à relancer après une modification du code (relevé de référence dans `CLAUDE.md`).

## Aides à la traduction (après extraction)

Ces commandes ne lisent et n'écrivent que les fichiers de langue de `out\` (ou d'un dossier donné en chemin), jamais le jeu, le workshop ni un mod. Elles ne remplissent **que** les entrées encore à `TODO` et gardent tous les commentaires. `--fill-tm` et `--fill-labels` ajoutent `<!-- Auto (...) : à relire -->` au-dessus de chaque valeur écrite, pour la relire (une réextraction retire ce commentaire mais garde la valeur).

### Mémoire de traduction : `--fill-tm`
```
extract.cmd --fill-tm ks.flooringfloors            (même mod seulement)
extract.cmd --fill-tm lucius.floorpatch --tm-global (puis tous les mods de out\)
```
`extract.cmd --fill-tm all` fait de même pour tous les mods de `out\` en une passe (une seule mémoire, construite sur tous avant de remplir).
Pour chaque `TODO`, recopie la traduction d'une autre entrée au même texte `EN:` exact : d'abord dans le même mod, puis (avec `--tm-global`) dans tout `out\`. Les entrées `ATTENTION` (anglais modifié depuis la traduction) ne servent pas de source. Si le même anglais a plusieurs traductions, la plus fréquente est prise et le cas est listé (« à harmoniser »). Le rapport donne le nombre d'entrées remplies et ce qui reste : `TODO` restants, textes anglais **distincts**, les plus répétés. Façon de faire : traduire une occurrence de chaque texte, puis relancer `--fill-tm`. Les listes (`<li>`) sont remplies aussi quand toute la liste anglaise est identique (24/11/2026).

**Contexte** (24/11/2026) : une traduction n'est reprise que du même type de def et du même endroit de la def (`label`, `tools/label`…) ; à défaut, du même champ d'un autre type dans le même mod ; sinon seulement si l'anglais a au moins 5 mots. Si les traductions connues divergent, la plus fréquente doit couvrir deux tiers des cas. Les couples (type, anglais) de `Dibolic_FrenchModsPackRemplissage_exclusions.csv` (`source` = `memoire`) ne sont jamais repris.

### Traduction officielle : `--fill-official`
```
extract.cmd --fill-official all --dry-run --to rapport.csv   (essai : rien n'est écrit)
extract.cmd --fill-official sarg.alphaanimals
```
Remplit les `TODO` avec `Officiel.csv` du pack, pour le même anglais exact, le même type de def et le même endroit (même clé pour les Keyed). Laissés à `TODO` : plusieurs traductions officielles, terme du glossaire différent, `Remplissage_exclusions.csv` (`source` = `officiel`). Commentaire ajouté : `Auto (officiel) : à relire`. À lancer avant `--fill-tm`.

### Libellés « matériau × motif » : `--label-tables`, `--fill-labels`
Pour Flooring Floors et ses mods de patchs, dont les libellés combinent un matériau et un motif (« sapphire crosshatch M tile », « V cocoawood strip floor »).
```
extract.cmd --label-tables ks.flooringfloors,lucius.floorpatch
   (remplir out\ks.flooringfloors\label-patterns.csv et label-materials.csv)
extract.cmd --fill-labels ks.flooringfloors,lucius.floorpatch
extract.cmd --fill-labels ks.flooringfloors,lucius.floorpatch --relabel   (après correction des tables)
```
- `--label-tables` décompose chaque `.label` des fichiers `DefInjected\TerrainDef` (option `--label-folder`) en un gabarit de motif et un matériau, puis écrit ou **met à jour** deux tables (par défaut dans `out\<premier id>\`, option `--tables <dossier>`). Les valeurs françaises existantes sont gardées, les comptes sont mis à jour.
  - `label-patterns.csv` : `en;fr;count;example`, ex. `{M} crosshatch tile;carrelage en croisillon de {M};94;…`
  - `label-materials.csv` : `en;fr;elision;count;example;suggestion`. `suggestion` = français déjà donné au même libellé anglais dans `out\` (nos traductions des mods sources), à recopier dans `fr` après vérification.
  - CSV : séparateur `;`, UTF-8 avec BOM (s'ouvre dans Excel), guillemets `"..."`.
- La décomposition est déduite des libellés, pas des defNames (`KS_StoneGravelKObsidian` est « schorl gravel paving ») : chaque groupe de 1 à 3 mots est essayé comme matériau ; l'emporte la coupe dont le gabarit est commun au plus de matériaux et dont le matériau apparaît dans le plus de gabarits, puis quelques tours recoupent chaque libellé d'après la majorité (familles irrégulières comme « stainless steel arx tile »). Les coupes utilisées par moins de `--min-share` (3) libellés sont abandonnées : ces libellés (fautes de frappe, sans matériau) restent pour la mémoire ou la traduction manuelle.
- `--fill-labels` écrit le `fr` du motif en remplaçant `{M}` par le `fr` du matériau, pour les libellés à `TODO` dont le motif **et** le matériau sont traduits. Écrire le motif sans accord nécessaire (« carrelage en croisillon de {M} » → « carrelage en croisillon de saphir »). `de` + voyelle devient `d'` (« d'émeraude ») ; la colonne `elision` le force (`oui`, ex. h muet : « d'hématite ») ou l'empêche (`non`). `--relabel` régénère aussi les libellés qui portent encore le commentaire du générateur.
- Le rapport liste les motifs et matériaux non traduits qui bloquent le plus de libellés.
- Vérifier ensuite avec `--check`. `TerrainDef.label` a une limite souple (comme `ThingDef.label`) : la forme française « … de <matériau> » est en général plus longue, les dépassements sortent en `LONG (souple)`, pas en erreur.

### Feuilles de traduction : `--export-todo`, `--import`
```
extract.cmd --export-todo adamas.vendingmachines,zal.titlesforme [--to <fichier>] [--batch 300]
   (écrire le texte après le dernier « || » de chaque ligne)
extract.cmd --import feuilles\todo_adamas.vendingmachines+1_20261121-233000.txt
```
- `--export-todo` écrit chaque entrée encore à `TODO` (`all` = tous les mods de `out\`) dans un fichier texte, par défaut `feuilles\todo_<id>_<date>.txt` (n'écrase jamais une feuille existante ; `--batch N` coupe en fichiers de N entrées). Chaque entrée est une ligne `packageId || fichier sous Languages\French || clé || `, avec au-dessus l'anglais et son contexte (contexte, limite, symboles à garder, français officiel, « texte anglais modifié »).
- Une liste a la clé `clé[]` ; ses éléments, en anglais comme en français, sont séparés par ` ¶ `. Le nombre d'éléments peut changer pour les listes de règles (`rulesStrings`).
- La traduction est du texte brut : `<color=…>`, `&` et `\n` s'écrivent tels quels, `--import` les échappe pour le XML. Les lignes vides sont ignorées (on peut traduire en plusieurs fois).
- `--import` ne remplace que les entrées **encore à `TODO`** (une entrée traduite depuis l'export est signalée, pas écrasée), vérifie que chaque fichier reste du XML valide avant de l'écrire, et liste les mods touchés. Ensuite : `Fix-Typography.ps1 -Path out\<mod>`, `--check`, et la synchro de ces mods.

### Comparaison avec une traduction tierce : `--compare-ref`
```
extract.cmd --compare-ref bodlosh.weaponstats --ref 2798150558
```
- `--ref` : le mod de traduction (packageId, ID Workshop, nom) ou un dossier. Tous ses dossiers `Languages\French*` sont lus (celui de la version du jeu l'emporte si une clé est dans plusieurs). Lecture seule ; écrit `out\<id>\compare-ref.csv` (`etat;type;cle;anglais;nous;reference`).
- Clés traduites des deux côtés : identiques ou différentes, typographie mise à part (guillemets, espaces avant la ponctuation, casse). **Les libellés courts traduits autrement** (anglais de 40 caractères au plus) sont affichés : c'est là que se voient les contextes manqués (novembre 2026 : *sway* lu comme un recul, *belt* non traduit par l'officiel « utilitaire »).
- Clés présentes seulement dans la référence, cherchées dans le mod cible (qui doit être installé) : la def existe dans la version actuelle et a ce champ (son XML ou ses parents abstraits), ou la clé exacte est dans son English DefInjected (`extraction ?` : texte que notre extraction a pu manquer, à vérifier) ; la def existe sans ce champ (`champ absent du mod actuel` : ancienne clé) ; seulement dans d'autres fichiers du mod (`ancienne version`), ou nulle part (`absente du mod` : autre mod couvert par le même pack de traduction, ou clé retirée).

- Valeur `{Clé.SousClé}` entière (réglage XML Extensions, ex. Some More Jobs) : non extraite, c'est le texte saisi par le joueur (24/11/2026).

### Champs servant d'identifiant déjà extraits : `--audit-keys`
```
extract.cmd --audit-keys all
```
- Pour les extractions antérieures au filtre « champ servant d'identifiant » (voir *Champs et textes*). Une entrée de `out\` ne garde pas le type de son champ : la correspondance se fait par nom de champ, limitée aux types déclarés par le jeu, le mod, ses dépendances et les mods qu'il charge avant lui ; un nom `[MustTranslate]` dans ces assemblies est ignoré (ambigu : `label`, `description`), ainsi que les champs `debug*` et les textes de quête. Ce sont des candidates à vérifier : la réextraction du mod applique la règle exacte.
- Lecture seule du jeu, du workshop et de `out\` ; écrit `out\audit-keys.csv` (`mod;fichier;cle;anglais;traduction;champ;usage`) et affiche les totaux par champ et les mods dont des entrées sont traduites.
- Relevé du 27/11/2026 : 73 candidates dans 12 mods, 12 traduites, toutes sans effet (`ScenPart_GameStartDialog.textKey` traduit par lui-même ; Stats `ColumnDef.labelKey` `Damage`/`Range` déjà traduits par l'officiel en Keyed). Les autres sont à `TODO`, donc pas copiées dans le pack.
- Diagnostic : `--field-usage "<dll1,dll2|@liste.txt>|filtre"` liste les champs utilisés comme identifiant dans ces DLL et la méthode en cause.

## Limites connues

- Patchs `PatchOperation` en C# (classe personnalisée) qui n'enveloppent pas d'autres opérations : non exécutés, listés dans le rapport (`MakeGunCECompatible`, `AddOrMerge`, `SliderFloat`…).
- Libellés d'options écrits dans un patch (`label`, `tooltip` de `ModSettingsFramework.PatchOperationModOption`) : non extraits.
- Textes générés par le code du jeu non extraits : recettes/terrains implicites (Make_*, roche brute),
  valeurs par défaut calculées dans du code (non littérales), `debugLabel`. Le jeu génère ces recettes avec le Keyed de la langue.
- Clés que le mod ne donne que dans son `English\DefInjected` : reprises sans vérifier que le champ existe encore ; une clé périmée est refusée par le jeu, sans autre effet (ex. `DMS_ProcessDocumentQuest.root.nodes.6.label`, 24/11/2026).
- Clé renommée ou def déplacée dans un **autre fichier** : la traduction n'est pas reprise (elle reste en `OBSOLETE` dans l'ancien fichier).
- Doublon entre un mod et le patch d'un autre (`tools.barrel` / `tools.barrel-0`, `verbs.Rocket`) : non détecté, une ligne « Duplicate » dans le rapport du jeu.
