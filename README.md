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
`--skip-shipped` (ne pas extraire les entrées que le mod traduit déjà lui-même).

## Comportement
- Relancer fusionne : les traductions déjà saisies sont conservées, les EN modifiés sont signalés
  (`ATTENTION`), les clés disparues passent en commentaire `OBSOLETE`.
- Textes ajoutés/modifiés par les patchs du mod sur d'autres mods/Core : fichiers `Patches_<fichier>.xml`.
- `context-rules.json` : règles de contexte/longueur éditables (premier match gagne). Une règle avec `"soft": true` (noms d'objets : `ThingDef.label`, `labelPlural`) produit une « Limite souple » : `--check` signale un dépassement en `LONG (souple)` sans le compter comme erreur.
- `report.txt` : comptes, patchs C# non exécutés, types introuvables, champs texte non marqués ignorés.

- Si le mod embarque déjà du français (`Languagesrench*`), l'outil l'annonce en console et dans `report.txt` (section « traduction déjà fournie par le mod ») : couverture, entrées manquantes, clés obsolètes, anciennes clés indexées (`comps.0.tools.0`). la traduction du mod n'est jamais modifiée.

## limites connues
- Patchs `PatchOperation` en C# (classe personnalisée) non exécutés : listés dans le rapport.
- Textes générés par le code du jeu (labelPlural/lifeStages des PawnKindDef, recettes/terrains implicites,
  valeurs par défaut) non extraits.
- Contexte d'affichage heuristique (règles + analyse des chaînes des DLL).
