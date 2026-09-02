# Génération hors ligne des textures PBR

Le générateur `tools/PbrTextureGenerator` produit des cartes de normales, roughness,
métallicité et émission à partir des textures diffuses du jeu ou d'un mod tiers décompressé. Il ne
modifie jamais les textures sources. Les sorties sont isolées sous
`generated/pbr` (ou sous le dossier fourni avec `--output`).

**Frontière d'architecture : cet outil est exclusivement hors ligne.** Il n'est
pas référencé par le mod VintageRTX, ne contient aucun hook de jeu et ne doit
jamais être appelé au chargement d'un monde ou pendant une frame. Le jeu charge
uniquement des PNG déjà générés et packagés.

## Pourquoi ce générateur

Un filtre Sobel simple ne voit qu'un gradient local 3x3 et ne peut pas
adapter son interprétation au matériau. L'outil hors ligne utilise :

- la luminance linéaire plutôt que la moyenne sRGB ;
- un gradient Scharr multi-échelle, plus isotrope que Sobel ;
- un lissage bilatéral qui réduit le bruit sans effacer les joints ;
- un échantillonnage périodique aux bords pour les textures tuilables ;
- l'alpha pour ne pas faire déborder le relief dans les pixels transparents ;
- des profils `stone`, `brick`, `wood`, `metal`, `anvil`, `polished`,
  `polished-metal`, `cloth` et `glass` ;
- un gain de pente hors ligne de 0,90, borné à 0,28 (15,6° de tilt maximal) : la luminance d'un
  albedo est un indice de hauteur trop faible pour justifier un relief fortement
  embossé ;
- une roughness issue du profil, du contraste local et de la luminance.
- une métallicité quasi binaire pilotée par le profil : une pierre ou un panneau
  poli reste diélectrique, tandis qu'une enclume est un métal forgé plus rugueux
  qu'un métal poli ;
- une émission bornée aux texels lumineux
  des textures identifiées comme sources de lumière.

Ce procédé reste une estimation depuis l'albédo : un détail peint peut être pris
pour un relief. Une normal map créée par un artiste ou depuis une vraie height map
reste prioritaire quand elle existe.

## Pré-requis et conventions

Le projet référence `SkiaSharp.dll` fourni avec le jeu. Comme le mod principal,
il utilise `VINTAGE_STORY` pour retrouver l'installation. La valeur peut viser
le dossier du jeu ou son sous-dossier `assets`.

Compiler une première fois la solution (le script saute ensuite la restauration
NuGet, car il n'utilise aucun paquet externe) :

```powershell
dotnet build .\VintageRTX.sln -c Release
```

En mode `generate`, les fichiers de travail produits sont :

- `<texture>_n.png` : normal tangent-space RGBA8, convention OpenGL `+Y` ;
- `<texture>_r.png` : roughness perceptuelle en niveaux de gris, alpha conservé ;
- `<texture>_m.png` : métallicité linéaire en niveaux de gris, alpha conservé ;
- `<texture>_e.png` : intensité d'émission linéaire en niveaux de gris, alpha conservé ;
- `pbr-manifest.json` : ledger de build, paramètres, dimensions et SHA-256.

Le manifeste est déterministe et ne contient ni date ni chemin absolu. L'option
`--flip-green` permet de produire une convention DirectX `-Y` si un futur shader
l'exige.

## Convention ouverte inter-mod v3

### Cartes embarquées par le mod propriétaire

Un mod qui distribue ses propres PBR place les sidecars dans son domaine, à côté
de l'albédo :

```text
assets/<domain>/textures/.../granite.png
assets/<domain>/textures/.../granite_n.png
assets/<domain>/textures/.../granite_r.png
assets/<domain>/textures/.../granite_m.png
assets/<domain>/textures/.../granite_e.png
```

Cette disposition est zéro-configuration. Le runtime associe les fichiers par
le chemin d'asset complet et le suffixe. `_m` et `_e` sont optionnels pour les
anciens contenus. Une paire authored `_n/_r` est toujours prioritaire et le
packager hors ligne ne la régénère pas ; les sidecars authored `_m/_e` restent
chargés directement par le runtime.

Pour un manifeste v3, cette correspondance est stricte : les quatre cartes
doivent être adjacentes à l'albedo dans le même domaine, le SHA-256 de l'albedo
chargé doit encore correspondre au SHA-256 de génération, et les manifests sont
parcourus dans un ordre stable. Une carte générée devenue obsolète est ignorée ;
elle ne peut pas revenir ensuite par le scan des suffixes. En cas de double
revendication valide d'un même albedo, la première identité stable gagne et le
conflit est journalisé, au lieu de dépendre de l'ordre d'énumération des mods.

Pour préparer ces fichiers sans écrire dans les sources du mod, générer vers un
dossier de staging ayant `assets` comme racine, contrôler le résultat, puis
copier uniquement les sidecars validés dans le paquet du mod.

### Pack de base embarqué par VintageRTX

Le pack de base suit le contrat normal d'un mod Vintage Story. Les cartes sont
présentes dans l'arbre source et copiées telles quelles par MSBuild :

```text
src/VintageRTX/assets/vintagertxbasepbr/config/vintagertx/pbr-manifest.json
src/VintageRTX/assets/game/textures/.../<albedo>_n.png
src/VintageRTX/assets/game/textures/.../<albedo>_r.png
src/VintageRTX/assets/game/textures/.../<albedo>_m.png
src/VintageRTX/assets/game/textures/.../<albedo>_e.png
```

Les suffixes exacts `_n`, `_r`, `_m` et `_e` identifient les sidecars ; leur
présence dans `assets` n'en fait pas des albedos. VintageRTX les retire de
`BakedVariants` et `BakedTiles` immédiatement après
l'expansion des wildcards `CompositeTexture`, avant l'enregistrement dans les
atlas couleur des blocs, objets, entités et shapes. Les fichiers restent des
assets ordinaires dans leur domaine et demeurent accessibles au chargeur PBR.
VintageRTX construit ensuite la table
des albedos uniquement depuis les références réellement bakées dans l'atlas,
puis cherche les quatre sidecars adjacents pour cette source canonique. Le
manifeste `vintagertx.pbr-manifest` version 3 associe explicitement chaque
source `domain` + `textures/...png` à ses quatre AssetLocations natives. Chaque
carte est obligatoirement obtenue en remplaçant `.png` par `_n.png`, `_r.png`,
`_m.png` ou `_e.png`, sans arbre `textures/pbr`, atlas central ni chemin absolu. Il enregistre
séparément `origin` (dossier physique sous `assets`) et `domain` (domaine logique
de l'AssetLocation), ainsi que le mod et la version source, les SHA-256, les dimensions, le profil
et les encodages. Son schéma formel est
[`pbr-pack-manifest-v3.schema.json`](pbr-pack-manifest-v3.schema.json). Le runtime
continue d'accepter les manifestes v1/v2 historiques, mais le packager ne produit
plus que la disposition native v3.

Pour les textures vanilla dont l'origine physique est `survival` ou `creative`
mais le domaine logique est `game`, le ZIP contient donc exactement :

```text
assets/game/textures/<même chemin relatif>/<stem>_n.png
assets/game/textures/<même chemin relatif>/<stem>_r.png
assets/game/textures/<même chemin relatif>/<stem>_m.png
assets/game/textures/<même chemin relatif>/<stem>_e.png
```

L'archive produite par l'outil reste un artefact de staging reproductible : elle
ne contient jamais les albedos sources, aucun DLL et aucun générateur. Elle ne
fait pas partie du build Release. Après validation, ses 28 824 sidecars et son
manifeste sont matérialisés une fois dans `src/VintageRTX/assets`; le build du
mod ne génère aucune image et se contente de copier `assets/**`.

## Commandes

Lister les profils :

```powershell
dotnet run --project .\tools\PbrTextureGenerator\PbrTextureGenerator.csproj -- profiles
```

Valider l'algorithme sur des fixtures synthétiques :

```powershell
dotnet run --project .\tools\PbrTextureGenerator\PbrTextureGenerator.csproj -c Release -- self-test
```

Valider un artefact PBR avant son intégration dans les assets du mod, sans lancer le jeu :

```powershell
dotnet run --project .\tools\PbrTextureGenerator\PbrTextureGenerator.csproj `
  -c Release -- validate-pack `
  --input '.\generated\pbr-packs\base\vintagertxbasepbr-1.0.0.zip' `
  --source-assets 'D:\Jeux\Vintagestory\assets' `
  --deep
```

Le validateur contrôle le chemin de manifeste indexable, `modinfo.json`, le
schéma v1/v2, l'unicité des clés atlas `domain:path`, la présence des cartes,
leurs suffixes, dimensions et SHA-256. `--source-assets` ajoute la vérification
des origines physiques et des SHA des albedos en lecture seule. `--deep` décode
tous les PNG et contrôle la normalisation des normales ainsi que l'encodage
monochrome des cartes scalaires. Sans ces deux options, le contrôle structurel
reste rapide et autonome, donc adapté à une CI de mod tiers.

Mesurer la distribution angulaire de toutes les normal maps d'un pack :

```powershell
dotnet run --project .\tools\PbrTextureGenerator\PbrTextureGenerator.csproj `
  -c Release --no-build -- analyze-normals `
  --input '.\generated\pbr-packs\base\vintagertxbasepbr-1.0.0.zip'
```

Cette analyse décode les PNG en RGBA non prémultiplié, contrôle la longueur
des vecteurs et rapporte les angles pixel ainsi que le RMS par texture et par
profil. Elle permet de détecter une régression vers des cartes numériquement
valides mais visuellement plates sans démarrer Vintage Story.

Générer un dossier complet tout en conservant les chemins de domaine :

```powershell
.\tools\generate-pbr.ps1 `
  -AssetsRoot 'D:\Jeux\Vintagestory\assets' `
  -InputPath 'survival\textures\block\stone' `
  -OutputRoot '.\generated\pbr'
```

Générer un lot de validation sans copier les textures originales :

```powershell
.\tools\generate-pbr.ps1 `
  -AssetsRoot 'D:\Jeux\Vintagestory\assets' `
  -InputPath @(
    'survival\textures\block\stone\brick\granite1.png',
    'survival\textures\block\wood\planks\acacia1.png',
    'survival\textures\block\metal\anvil\copper.png',
    'survival\textures\block\cloth\cloth-stitched.png',
    'survival\textures\block\glass\leaded.png'
  ) `
  -OutputRoot '.\generated\pbr\validation'
```

Créer un artefact/staging PBR pour un autre mod décompressé :

```powershell
.\tools\package-mod-pbr.ps1 `
  -ModRoot 'E:\ModsSources\beautifulblocks' `
  -PackId 'beautifulblockspbr' `
  -PackVersion '1.0.0' `
  -OutputRoot '.\generated\pbr-packs'
```

Le `.zip` produit par cette commande ne doit pas être déposé tel quel dans
`Mods`. Copier les sidecars validés dans le mod propriétaire pour bénéficier de
la convention authored via l'asset manager ; l'archive séparée reste uniquement
un artefact de staging hors ligne. Le dossier source doit contenir `modinfo.json` et
`assets/<domain>/textures`.
Par défaut seules les textures `block` sont parcourues ; `-Scope all` étend la
génération aux autres catégories de textures. Les couples authored `_n/_r` déjà
présents dans le mod source sont détectés et exclus du pack séparé. `PackId` doit
contenir uniquement des minuscules ASCII et des chiffres, comme l'impose la
[référence officielle de modinfo](https://wiki.vintagestory.at/Modding%3AModinfo).

Créer un pack depuis un dossier `assets` sans `modinfo.json`, par exemple les
assets de base installés, exige une identité source explicite :

```powershell
.\tools\package-assets-pbr.ps1 `
  -SourceAssets 'D:\Jeux\Vintagestory\assets' `
  -SourceModId 'game' `
  -SourceModVersion '1.22.7' `
  -DomainMap @('game=game', 'creative=game', 'survival=game') `
  -PackId 'vintagertxbasepbr' `
  -PackVersion '1.22.7' `
  -OutputRoot '.\generated\pbr-packs\base' `
  -Scope all
```

`DomainMap` accepte plusieurs valeurs `origin=domain`. Seules ces origines sont
parcourues ; aucun `modinfo.json` voisin n'est lu ou supposé dans ce mode. Cette
distinction est nécessaire pour les assets vanilla : les fichiers résident sous
les origines physiques `game`, `creative` et `survival`, mais leurs AssetLocations
utilisent toutes le domaine logique `game`. L'ordre est fourni de la priorité la
plus faible à la plus forte ; en cas de collision logique, la dernière origine
gagne et le rapport conserve les deux SHA-256.

## Intégration à l'atlas PBR runtime

Après le bake de l'atlas vanilla, le runtime lit le manifeste embarqué et les
sidecars adjacents via `IAssetManager`, puis applique uniquement les
correspondances exactes de l'albédo réellement présent dans l'atlas. Une carte authored fournie par un
mod au même `domain:path` est donc prioritaire. Si ses octets diffèrent du
fallback, le SHA du manifeste permet de l'identifier comme authored et le
passage sidecar charge cette version. Pour les packs publics historiques, le
runtime peut aussi parcourir les `IAssetOrigin` et retenir la dernière variante
dont le SHA ne correspond à aucun fallback déclaré. Le pack de base embarqué ne
constitue aucun chemin de chargement distinct. Le runtime ne génère aucune texture. Pour
chaque texture d'albédo de l'atlas, la clé stable est son domaine et son chemin,
par exemple :

```text
game:textures/block/wood/planks/acacia1.png
```

Les fallbacks dérivés de l'albédo sont bornés une seconde fois après le
redimensionnement vers le rectangle d'atlas : leur pente tangentielle ne peut
dépasser `0,28`, soit 15,6°. Cette calibration ne s'applique pas aux sidecars
authored, qui conservent leurs canaux XY exacts. Elle évite qu'un contraste
peint crée deux normales opposées séparées de plus de 35° et déclenche un
scintillement embossé à l'écran.

Les albedos comportant au moins un texel non opaque sont exclus uniquement du
fallback synthétique du manifeste. Sur les feuillages et autres cartes alpha,
des pixels voisins peuvent appartenir à des plans croisés dont les normales
géométriques divergent fortement ; une height map déduite de la luminance ne
peut pas représenter ce relief. Un sidecar authored reste prioritaire et est
donc toujours appliqué, y compris sur ces textures.

Le chemin des cartes `_n`, `_r`, `_m` et `_e` est dérivé du chemin de l'albédo ;
le manifeste ne fait que l'indexer et en vérifier les octets.
À la construction de l'atlas, copier les cartes dans des couches ayant exactement
le même rectangle UV que l'albédo, avec un padding identique. Les normal maps
doivent être échantillonnées comme données linéaires (pas sRGB) ; la roughness est
ainsi que la métallicité et l'émission sont également des données linéaires. Les fragments sans carte peuvent recevoir la
normale plate `(0.5, 0.5, 1.0)` et la roughness du matériau voxel.

Le bridge terrain consolide toutes les cartes dans un seul atlas RGBA et un seul
sampler : `R/G = normal XY`, `B = roughness`, `A = 3 bits metallic + 3 bits
emissive + 1 bit presence`. Le Z positif est reconstruit depuis XY. R/G/B sont
filtrés linéairement, tandis que le payload A est relu au texel exact avec
`texelFetch`, afin qu'aucune interpolation ne corrompe les catégories. Cette
disposition supprime un atlas 4096×4096 RGBA8 et un binding par rapport au chemin
historique séparé. Le bridge choisit l'unité fragment disponible la plus haute en
excluant 15/16. La réponse tangentielle 1,35 est une constante du shader : elle
ne dépend pas d'un uniforme par draw pouvant retomber à zéro.

Pour préserver les FPS et garantir un comportement reproductible, le runtime ne
fait que charger un atlas déjà construit et vérifier les SHA-256 disponibles. Un
asset sans PBR reçoit une normale plate et la roughness matérielle par défaut ;
il ne déclenche jamais de calcul d'image.

### Propriété sûre des rectangles d'atlas

`BakedCompositeTexture.TextureFilenames` contient le nom de base puis ses
overlays ; ce n'est pas une liste d'albedos interchangeables. VintageRTX ne lie
donc un sidecar à un rectangle que lorsque le nœud baked possède exactement une
texture source. Les composites base+overlay sont laissés au matériau neutre tant
qu'une composition PBR reproduisant fidèlement les opérations de bake du jeu
n'est pas disponible.

Après collecte, chaque rectangle est regroupé par identité exacte
`atlasTextureId + atlasNumber + UV`. Si plusieurs sources distinctes revendiquent
le même rectangle, toutes les liaisons de ce rectangle sont refusées : aucun
ordre de manifeste, de domaine ou de sidecar ne peut devenir un mécanisme
« dernier écrivain gagnant ». Le fallback par index direct est également bloqué
pour les sources vues dans une ambiguïté. Le journal runtime rapporte séparément
`exact placements`, `ambiguous rectangles`, `skipped ambiguous links` et
`skipped composite rectangles`.

## Limites et contrôle qualité

- L'auto-classification est basée sur le chemin ; forcer `--profile` pour un cas
  atypique.
- L'émission générée automatiquement exige à la fois un chemin d'émetteur connu
  et une chromaticité suffisante. Les texels blancs peu saturés restent non
  émissifs afin que neige, verre et reflets peints ne deviennent pas des lampes.
- Le verre coloré reçoit un relief très faible et une roughness basse ; sa
  transmission devra rester gérée par le shader.
- Une texture contenant déjà le suffixe sidecar exact `_n`, `_r`, `_m` ou `_e` est ignorée
  pour éviter les générations récursives. Les noms d'albedo comme
  `side_normal.png` restent valides et sont traités.
- Le packager considère une paire `_n` et `_r` complète comme authored et la
  laisse dans le mod propriétaire. Une paire incomplète n'est jamais modifiée ;
  le pack séparé génère sa propre paire complète sous son propre domaine.
- L'outil écrit atomiquement seulement dans le dossier de sortie. Les SHA-256
  source avant/après sont comparés et les tests utilisent une source en lecture
  seule.

## Validation locale de référence

Le lot synthétique de validation est généré sous `generated/pbr/validation`
sans recopier les albedos. Le test automatique a
également vérifié l'immuabilité d'une source en lecture seule, le résultat PNG et
manifeste identique octet par octet, la normalisation des vecteurs, la couture
tuilable, l'ordre de roughness entre profils et un ZIP de mod tiers synthétique.
Ce dernier test vérifie aussi le manifeste v3, la disposition adjacente par domaine,
l'absence d'albedo redistribué, la
priorité aux sidecars authored, l'identité SHA-256 de deux builds et la validation
autonome profonde des huit cartes du pack.

| Classe physique | Profil | Roughness de base | Métallicité |
| --- | --- | ---: | ---: |
| pierre mate | `stone` | 0,86 | 0,00 |
| panneau ou pierre polie | `polished` | 0,20 | 0,00 |
| métal courant | `metal` | 0,42 | 0,92 |
| enclume / métal forgé | `anvil` | 0,48 | 0,96 |
| métal poli | `polished-metal` | 0,16 | 0,96 |
| tissu | `cloth` | 0,90 | 0,00 |
| verre | `glass` | 0,08 | 0,00 |

Ces valeurs confirment que les cartes ne sont ni plates ni saturées, et que la
hiérarchie de matériau attendue est respectée. Elles ne remplacent pas la
validation visuelle en jeu sous la lanterne de la scène de référence.

### Pack de base complet Vintage Story 1.22.7

Le mode `source-assets` a produit l'artefact de génération déterministe :

```text
generated/pbr-packs-1.22.7-full/vintagertxbasepbr-1.22.7.zip
```

Ce `.zip` source sert à la validation et au staging uniquement : il ne doit
jamais être copié directement dans `Mods` et le build n'en dépend pas. Les
fichiers validés sont versionnés sous `src/VintageRTX/assets` puis copiés comme
ressources normales du mod.

Résultat du build complet :

- 9 585 albedos physiques sous `game`, `creative` et `survival` ;
- 9 582 identités logiques après trois overrides vanilla (deux identiques,
  un divergent) ;
- 9 582 cartes pour chacune des quatre familles `_n/_r/_m/_e`, soit 38 328
  sidecars ;
- 38 330 entrées ZIP avec `modinfo.json` et le manifeste ;
- 74 003 129 octets, générés en 3 min 55,664 s ;
- SHA-256 ZIP `55c4835154bf536a9c98510b8eaec892d51d1f4e84e60a73f4924b4ecc0ba7e6` ;
- 0 albedo dans le ZIP et aucun chemin logique dupliqué ;
- validation profonde des 9 582 SHA source et des 38 328 SHA/cartes.

La mesure historique des 5 749 334 texels opaques des 7 206 normal maps de blocs donne un angle médian
1,71°, p90 7,35°, p95 10,28° et p99 17,86°. Le RMS médian par texture
est 3,16° ; 4 358 textures restent sous 4°. L'erreur moyenne de longueur après
quantification RGBA8 est 0,00094, avec un maximum de 0,00554. Les 21 textures
classées `glass` restent volontairement presque plates (RMS médian 0,32°),
tandis que les sept enclumes conservent un relief mesuré (RMS médian 1,45°).

Répartition automatique : 7 `anvil`, 308 `brick`, 333 `cloth`, 21 `glass`,
465 `metal`, 165 `polished`, 1 265 `stone`, 1 097 `wood` et 3 545 `generic`.
Le profil `polished-metal` reste disponible pour les mods tiers même si aucun
chemin du lot vanilla ne porte simultanément les marqueurs poli et métal. Les albedos légitimes
`side_normal.png` et `top_normal.png` sont couverts ; seul le suffixe sidecar
exact `_n`, `_r`, `_m` ou `_e` est réservé.

Le 2 septembre 2026, le pack complet a été régénéré directement depuis
l'installation officielle Vintage Story 1.22.7 avec l'algorithme
`vintagertx-pbr-v6`. Les sidecars sont versionnés dans le dossier source du mod ;
une Release ne les régénère pas.

Le pack initial utilisait une catégorie d'asset inconnue et non indexée. La
migration déterministe d'un ancien manifeste vers la catégorie publique `config`
reste disponible :

```powershell
dotnet run --project .\tools\PbrTextureGenerator\PbrTextureGenerator.csproj `
  -c Release --no-build -- repack-manifest `
  --input '.\generated\pbr-packs\base\vintagertxbasepbr-1.0.0.zip'
```

La correction d'un pack v3 qui confond origine et domaine déplace également les
sidecars vers le domaine logique et réécrit leurs AssetLocations, sans recalculer
les PNG :

```powershell
dotnet run --project .\tools\PbrTextureGenerator\PbrTextureGenerator.csproj `
  -c Release --no-build -- remap-domain `
  --input '.\generated\pbr-packs\base\vintagertxbasepbr-1.0.0.zip' `
  --origin survival --asset-domain game
```
