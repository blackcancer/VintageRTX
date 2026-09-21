# R01c — taille des sources et groupes de bougies dynamiques

Branche : `dev/renderer-rewrite-20260921`, sans branche supplémentaire. Base de ce lot : `15ab2119c0c53c5b7506b9edfae2faf9655429ba`.

## Périmètre réel

Ce lot prolonge le catalogue d'émission et les requêtes GPU. La nouvelle image PBR du monde n'est toujours pas raccordée : le renderer natif reste affiché. Les tests de fonctions, de transferts et de contexte OpenGL ne sont pas une réception visuelle du jeu. Les anciennes suites de cette branche sont conservées.

## Chandeliers et bougies cumulées

Le nombre n'est pas fixé à huit. Le catalogue comporte des `componentCounts` associant le **code runtime courant** à une quantité. Il couvre `chandelier-*-candle0` jusqu'à `candle8`, les groupes au sol `bunchocandles-1` à `bunchocandles-9` et les aliases `candles-N` déjà couverts par le catalogue. Une bougie simple reste une composante. Les correspondances sont des données patchables, pas une reconnaissance de famille codée en C#.

L'observateur existant relit le bloc actuel après les événements bloc/chunk, et surveille les émetteurs potentiels entre ces événements. Son code courant est résolu à nouveau ; une ancienne quantité n'est pas attachée définitivement à la position. La source conserve son identité de propriétaire. Le retrait complet enlève la source ; un chandelier sans bougie ou sans émission runtime ne produit aucune énergie. Le nombre d'objets dans un ItemStack n'est jamais pris pour un nombre de mèches.

Pour ce lot, le groupe est **encore une source spatiale agrégée**. Son signal est la moyenne des fluctuations de N composantes indépendantes, avec une graine stable par propriétaire et ordinal. Le fournisseur a déjà calculé l'intensité totale du groupe :

`intensité_evaluée = intensité_runtime_totale * somme(modulation_mèche_j) / N`

Ce choix évite de multiplier deux fois l'énergie par le nombre de bougies. Ajouter/retirer une composante ne remet ni l'horloge ni les graines existantes à zéro. Les sources stables, les locustes pilotés par le moteur et la foudre gardent une seule enveloppe, même si une configuration leur attribue plusieurs composantes.

**Limite conservée explicitement :** positions physiques, orientations, masques et ombres individuelles des mèches ne sont pas implémentés. Pour les groupes au sol, le modèle officiel applique aussi une rotation dérivée de la position ; une future décomposition spatiale devra reprendre cette rotation et les véritables mèches, pas dessiner neuf lumières arbitraires. Une modification physique du bloc continue d'invalider son maillage par le chemin géométrique indépendant.

Les règles `componentCounts` se résolvent par code exact, puis spécificité littérale. Les motifs contradictoires de même rang sont refusés. Maximum 128 motifs par règle et quantités entières entre 0 et 64. Sans motif correspondant, la représentation reste l'agrégat à une composante, pas un nombre prétendument mesuré. Les variantes supplémentaires de mods doivent fournir leurs propres correspondances. Un changement de profil du chandelier affecte toutes ses quantités sans réécrire chacune d'elles.

Sources officielles examinées :
- https://github.com/anegostudios/vssurvivalmod/blob/master/Block/BlockChandelier.cs
- https://github.com/anegostudios/vssurvivalmod/blob/master/Block/BlockBunchOCandles.cs
- https://github.com/anegostudios/vssurvivalmod/blob/master/Block/BlockCandle.cs

## Taille lumineuse configurable

Le champ optionnel `sourceRadius` d'une règle ou de `attributes.vintageRtxEmission` exprime un rayon sphérique équivalent en **unités de bloc**, entre 0 et 16. Il est distinct de la portée. Zéro conserve une source ponctuelle. Le catalogue de base reste à zéro par défaut : ce lot n'impose pas de nouvelles dimensions approximatives aux modèles du jeu.

Exemple de patch natif dans `assets/monpack/patches/emission.json` :

```json
[
  {
    "file": "vintagertx:config/emission.json",
    "side": "client",
    "op": "add",
    "path": "/bindings/vintagertx:candle/sourceRadius",
    "value": 0.018
  },
  {
    "file": "vintagertx:config/emission.json",
    "side": "client",
    "op": "replace",
    "path": "/profiles/vintagertx:candle/amplitude",
    "value": 0.08
  },
  {
    "file": "vintagertx:config/emission.json",
    "side": "client",
    "op": "replace",
    "path": "/bindings/vintagertx:chandelier/profile",
    "value": "vintagertx:steady"
  }
]
```

Les valeurs de l'exemple sont artistiques. Une taille équivalente n'est pas une mesure de la vraie flamme. Le troisième patch désactive le vacillement supplémentaire, pas l'autorité runtime d'allumage/extinction ni les correspondances de quantité.

Une configuration incorrecte ne remplace pas le catalogue valide. Un override du seul profil ne peut plus masquer une ambiguïté d'intensité, d'activation ou de rayon entre règles concurrentes. Une ambiguïté de quantité demande de corriger les règles/priorités.

## Estimateur de source finie

`FiniteEmitterSampling` (référence C#) et `sampleEmitterSegment`/`queryEmitterIncident` (GLSL) échantillonnent l'angle solide d'une sphère uniformément émissive, orientée vers l'extérieur. Pour une intensité radiante équivalente I et un rayon R : `L = I/(pi*R²)` et `Phi = 4*pi*I`. Modifier R conserve donc la puissance totale, contrairement à un simple rayon d'ombre multiplié arbitrairement.

Le poids `L/pdf` est rationalisé pour conserver la limite ponctuelle lorsque R/d devient très petit. Le segment de visibilité se termine sur la surface proche échantillonnée, pas au centre de la source. Les obstacles restent évalués : aucune exemption pour une main, une lanterne ou la cellule de l'émetteur. Un récepteur à l'intérieur de la sphère est explicitement non pris en charge par ce modèle de surface ; ce cas ne devient pas un point lumineux ou un rayon libre.

La requête diffuse prend jusqu'à 64 directions de source, 8 par défaut pour les sources finies et une seule pour les points. Les paramètres et intensités sont lus une fois par source. La moyenne inclut toutes les directions, y compris occultées ou non résolues ; normaliser seulement les directions libres ferait disparaître les pénombres. Une sphère peut dépasser l'horizon du récepteur même lorsque son centre est derrière lui : la borne géométrique utilise aussi son rayon.

Le même `LightFrame` évalué reste la source des données directes et secondaires. Pas de nouveau bruit de flamme dans le shader. `queryPointIncident` conserve son contrat ponctuel et refuse les sources finies ; les nouveaux consommateurs utilisent la requête finie explicitement. Le `ReferenceTracer` multirebond préexistant reste ponctuel : ce lot teste l'estimateur fini séparément et ne prétend pas lui avoir ajouté le transport surfacique multirebond.

Référence mathématique : https://www.pbr-book.org/4ed/Shapes/Spheres

## Validation

Lire les résultats du workflow du commit testé ; aucun résultat n'est présumé ici. Nouveaux contrôles : transitions de quantité, conservation de la somme runtime, graines stables et profils sans flamme ; comparaisons avec le getter de quantité de la DLL officielle et avec les variantes des assets installés ; taille patchable, rejet transactionnel des erreurs, sphères contre un oracle de solide projeté et d'intersection.

Les tests GLSL de source finie utilisent volontairement un fournisseur de visibilité analytique (plan, demi-plan, état inconnu) pour isoler la radiométrie. Ils ne remplacent pas la vraie traversée : les suites existantes de scène régionale et d'upload C# continuent de l'exécuter séparément. Une mutation du dénominateur démontre que la pénombre serait perdue en renormalisant seulement les échantillons non occultés.

Aucun gain FPS n'est annoncé. Une source finie coûte davantage de requêtes qu'un point. La sélection spatiale, l'image PBR en jeu, les mains, la décomposition spatiale des mèches, les reflets et la GI restent des travaux ouverts, pas des fonctionnalités certifiées par ces tests.
