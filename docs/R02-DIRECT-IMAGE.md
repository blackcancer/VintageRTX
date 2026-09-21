# R02 — première passe d'image d'éclairage direct

Branche conservée : `dev/renderer-rewrite-20260921`. Base : `68d0b8ea8ee4bb9b915ca64c504f805d4e285220`.

## Ce qui est raccordé

`DirectSurfaceFrame → SceneTextureSet + LightTexture → DirectImagePass → radiance HDR + diagnostics + aperçu`.

La passe consomme les véritables tables géométriques régionales et lumineuses du nouveau backend. Elle n'est plus un appel de fonction d'éclairage sur un unique pixel de test : elle calcule chaque récepteur d'une image, avec son matériau, sa normale et son segment d'occultation. Elle ne reconstruit jamais l'albedo depuis une image éclairée.

**La production des récepteurs depuis le rendu du monde n'est pas encore raccordée.** Ce lot fournit un laboratoire synthétique opt-in, dont les rayons primaires sont calculés sur des triangles CPU. Le rendu natif du monde reste actif. L'aperçu n'est ni une capture du monde du joueur, ni une réception visuelle du mod.

## Données de surface et cohérence

`DirectSurfaceFrame` est immuable. Ses quatre images RGBA32F portent position relative à l'ancre entière, normale de surface et index matériau, normale géométrique, et réflectance RGB linéaire non éclairée. L'origine est soustraite en double précision avant conversion float. La ligne zéro est en bas. Un récepteur absent demeure absent : aucune dilatation de silhouettes ou reconstruction depuis les voisins.

La table de matériaux contient Lambert et conducteur GGX isotrope avec indices complexes RGB eta/k. Le Fresnel du conducteur est calculé directement, sans spéculaire blanc recoloré ensuite ni lobe diffus ajouté au métal. La normale géométrique borne l'hémisphère réel tandis que la normale de surface pilote la réponse BSDF. Les deux ne sont pas interchangeables.

Les snapshots de surfaces, de géométrie GPU et de lumières doivent appartenir au même monde/dimension. Surfaces et géométrie partagent exactement le même snapshot ; lumières et surfaces la même ancre entière. Une révision incohérente, une ancienne frame lumineuse ou une erreur de rendu retire la disponibilité de l'image précédente. Une région non observée reste `Unknown`, jamais un espace confirmé vide.

Le compteur de révision géométrique devient accessible dans le paquet et le uploader expose son snapshot seulement après transfert réussi. L'uploader géométrique neutralise désormais également `UnpackSwapBytes` : ce paramètre étranger pouvait inverser les octets des tags entiers et des sommets float.

## Calcul de l'image

`material-query.glsl` complète les requêtes existantes. Le GGX possède une formulation stable du dénominateur à faible rugosité. L'intégration des sources sphériques emploie leur poids d'échantillonnage en angle solide ; le BSDF est évalué pour chaque direction et non seulement au centre de l'émetteur.

Les sources ponctuelles ne demandent qu'un rayon. Les sources finies utilisent 1 à 64 directions configurables (8 par défaut). L'intensité vient du `LightFrame` déjà évalué : pas de nouveau vacillement côté shader. Un échantillon occulté ou non résolu reste dans le dénominateur. Un miroir idéal de rugosité nulle est explicitement non pris en charge par cette passe de lumière directe ; il n'est pas artificiellement élargi pour fabriquer un reflet.

Trois sorties distinctes :

| Sortie | Format | Rôle |
|---|---|---|
| Radiance | RGBA32F | RGB linéaire non écrêté ; alpha marque le récepteur présent. |
| Diagnostics | RGBA32F | Nombre d'échantillons non résolus, occultés, tracés, puis statut. |
| Aperçu | RGBA8 | Affichage développeur uniquement, compression et transfert sRGB. |

Les statuts diagnostiques sont 0 (absent), 1 (complet), 2 (visibilité non résolue), 3 (récepteur non pris en charge), 5 (entrée invalide). Le magenta de l'aperçu rend les inconnues explicites ; il n'est pas une contribution au buffer HDR. Une source éteinte ne laisse pas d'énergie historique. L'exposition ne change jamais le HDR brut.

## Propriété des ressources et coûts

La passe possède ses cinq textures d'entrée de surface/matériaux et ses trois cibles de sortie. Aucun framebuffer du jeu n'est écrit par `DirectImagePass`. Le programme, VAO, framebuffers lecture/écriture, viewport, états de rasterisation, masques couleur et blend indexés, textures et samplers sont restaurés. Les allocations et uploads neutralisent PBO/strides et inversion d'octets. Entrée et sortie ne peuvent pas partager une texture.

À snapshot de surfaces identique, une variation de flamme n'entraîne ni nouvel upload de surfaces ni reconstruction géométrique. Le redimensionnement conserve les noms des textures privées et invalide l'image jusqu'au nouveau rendu. La passe requiert neuf samplers de fragment et trois sorties couleur.

Ce n'est pas une optimisation GPU qualifiée sur une carte du joueur. Les lectures d'états OpenGL, buffers RGBA32F et boucles sur toutes les sources sont une base de comparaison explicite. La capture raster native, la sélection spatiale des sources et les formats plus compacts doivent être évalués séparément. Aucun seuil de qualité ne doit être abaissé pour annoncer un gain.

## Laboratoire optionnel

Le panneau se commande côté client :

```text
.vrtxlightlab on
.vrtxlightlab dark
.vrtxlightlab lit
.vrtxlightlab off
```

Il est désactivé par défaut et n'exécute aucun upload/draw quand masqué. Sa première activation construit une scène fixe : quatre bandes de matériaux, relief de normale analytique sur une bande et un obstacle triangulé. Deux sources finies éclairent la scène. La source chaude consomme le profil patché du groupe de trois bougies ; la source froide reste stable. `dark` éteint les deux.

Les coordonnées optiques des conducteurs, intensités et tailles sont des valeurs de laboratoire **artistiquement définies**, pas des mesures d'un métal nommé ou d'une bougie réelle. La normale ondulée est analytique, pas une normal map provenant d'un pack. Les rayons primaires CPU sont construits une fois dans cette scène statique, pas recalculés sur tout le monde à chaque image. L'affichage est 128 × 96, agrandi par l'API de rendu 2D du jeu.

Les exceptions désactivent le laboratoire et sont signalées ; elles ne désactivent pas l'image native. Le départ du monde désactive le panneau. Une relance reconstruit ses ressources sur le thread de rendu.

## Validation à exécuter

Consulter le workflow du commit effectif pour les résultats, sans présumer leur réussite. Les nouvelles suites vérifient le paquet immuable, la conservation d'énergie, le Fresnel et le HDR, les transitions connu/inconnu/occulté, l'extinction, les changements d'origine et de révision, le redimensionnement et la restauration OpenGL sous état étranger hostile.

`DirectImagePassTests` compare chaque pixel d'une image 40 × 24 au calcul C# `DirectLightingReference`, incluant les comptes d'occultation. Le test exécute les uploaders et la vraie traversée géométrique, pas un fournisseur de visibilité toujours libre. Après réussite, la CI conserve `direct-hdr-rgba32f.bin`, `direct-image.json` et `direct-preview.ppm`.

La suite Python de matériaux compare en outre le GLSL aux amplitudes complexes de Fresnel calculées indépendamment, à la réciprocité et aux intégrales sous éclairage uniforme. Les suites précédentes de profils, quantités de bougies, patches natifs, meshes et traversée restent sélectionnées. L'adaptateur est compilé contre les DLL officielles de Vintage Story 1.22.7 ; aucune nouvelle bibliothèque du jeu n'est activée.

## Ce qui reste ouvert

Capture des récepteurs et textures du monde ; matériaux diélectriques spéculaires complets ; identification des matériaux par assets ; positions individuelles des mèches et attaches animées ; transparence/couverture alpha ; soleil/ciel ; reflets secondaires et GI ; débruitage temporel avec mouvement ; sélection spatiale ; mesures et réception en jeu.

Références :
- https://apidocs.vintagestory.at/api/Vintagestory.API.Client.IRenderAPI.html
- https://www.pbr-book.org/4ed/Reflection_Models/Roughness_Using_Microfacet_Theory
