# Transport secondaire et réception des retours de tests — 19 septembre 2026

Branche : `dev/renderer-recovery-20260918`.
Source du transport secondaire : `9370a330e98434ae75526c222736db0048d9975e`.
Source des matériaux RGB et du témoin embarqué : `641c4e4fea280dc964134348f2273031374ea68d`.
Aucune fusion dans main. Aucun seuil de réception visuelle ou de performances abaissé.

## Portée du lot

Les erreurs transmises par l'utilisateur ne désignent pas une cause unique. Le fichier PNG absent bloque des tests de lecture avant l'analyse d'image. Les scénarios `held-light`, `cave-interior` et `exterior-roof` lancent le client et produisent des mesures : leur code de retour 1 résume un ou plusieurs critères non satisfaits, pas nécessairement un crash. Leurs messages indiquent un arrêt volontaire du client par le banc de tests. Les extraits ne donnent pas l'identité complète du commit exécuté ; ne pas leur attribuer silencieusement une révision plus récente.

Les réparations du démarrage, de la projection miroir et des anciens tests de chaînes sont documentées dans `2026-09-19-test-blockers.md`. La capture RGB non éclairée, le modèle GGX et le témoin PNG embarqué sont décrits dans `2026-09-19-material-transport.md`. Le présent lot complète cette base par des corrections du rebond lumineux et de la précision géométrique.

## 1. Lampes et rebond indirect

Le shader précédent ne considérait que les deux premières lampes sélectionnées pour éclairer un impact secondaire. Une source chaude placée après ces deux entrées n'apportait donc aucun rebond dans ce chemin, même si elle contribuait à l'éclairage direct.

`traceVoxelDiffuseBounce` considère maintenant toutes les sources de la table active, toujours limitée à huit par le backend. Les rejets de portée, d'intensité nulle et d'orientation du récepteur précèdent les requêtes de visibilité. Une permutation de la même table ne doit plus retirer une lampe du transport secondaire.

La requête de visibilité conserve l'indice de l'émetteur. Elle peut donc utiliser son masque détaillé au bout du segment, au lieu d'interroger systématiquement une source anonyme avec l'indice -1. Cela conserve la distinction entre l'occultation d'une lampe chaude et celle d'une autre source.

Ce changement ne fournit pas une prise en charge illimitée des lumières, une cage mobile exacte ou un registre multi-sockets animé. Il ne certifie pas la disparition du défaut observé dans la grotte de l'utilisateur. Le coût des rayons supplémentaires reste à mesurer sur le vrai client.

## 2. Le soleil et le ciel ne partagent pas un résultat de visibilité

Un rayon dirigé vers une ouverture du ciel peut passer alors que le soleil est derrière un mur. Réutiliser sa visibilité pour le soleil apportait une énergie directe qui n'avait pas traversé le bon segment.

Le rebond utilise maintenant une requête solaire indépendante lorsque cette contribution est possible. Elle ne dépend pas du fait qu'un échantillon de ciel soit visible. Les forces du soleil, du ciel et des émetteurs sont appliquées à leur contribution respective.

Le parcours solaire utilise l'intégration native/voxel existante. Ce raccordement n'est pas une preuve de correction de la cible de toit dans `exterior-roof` : cet échec réel reste ouvert jusqu'à un contrôle dans la scène concernée.

## 3. Réflectance réelle et distribution des directions

La fonction ne remplace plus une surface noire par un minimum neutre de réflectance 0,10 et ne désature plus son albedo avant le rebond. Elle multiplie l'énergie incidente par le RGB linéaire du matériau rencontré. Une surface diffuse noire retourne zéro dans ce chemin.

Le rayon diffus était décrit comme cosinus-pondéré, mais sa projection sur le disque était contractée par 0,78. Cette restriction a été retirée pour couvrir le disque complet correspondant à l'hémisphère cosinus-pondéré.

La scène ne reçoit encore qu'un petit nombre de directions par pixel et les limites de distance et de couverture demeurent. Cette correction ne rend pas automatiquement l'intégrateur global non biaisé ou complet. Les matériaux rencontrés hors écran restent issus de la représentation voxel, pas de la nouvelle capture RGB du pixel primaire.

## 4. Précision des normales géométriques

Les dérivées de position des passes d'ombre et de composition sont calculées avant l'ajout de la grande origine mondiale flottante. Une translation constante n'apporte rien à une dérivée, mais son addition préalable peut perdre les petites différences entre pixels en simple précision.

Les normales de surface provenant des normal maps restent distinctes des normales géométriques utilisées pour la visibilité. Aucune amplification globale des normal maps n'a été ajoutée pour satisfaire artificiellement un seuil d'angle.

## 5. Témoin PNG et matériaux déjà inclus dans la branche

Le témoin réel `entity-mirror-local-body-real.png` est incorporé dans l'assembly de tests sous le nom `VintageRTX.Test.Fixtures.entity-mirror-local-body-real.png`. Le test ne cherche plus ce fichier dans un chemin absolu du checkout. Les mêmes octets servent au test des chemins Unicode ; l'empreinte SHA-256 originale reste contrôlée avant décodage. Un checkout sans l'original échoue explicitement à la compilation. Aucune image synthétique de remplacement n'a été générée.

La capture facultative `RawAlbedoCapture` produit du RGB non éclairé pour les shaders opaques compatibles. Quand la capture est disponible et sa profondeur cohérente, le rééclairage ne reconstruit plus la couleur du matériau depuis l'image déjà éclairée. Le calcul métallique utilise son F0 dans GGX, au lieu d'un Fresnel diélectrique blanc corrigé par plusieurs gains après coup. Voir le document du lot matériaux pour les limites des autres renderers, des surfaces hors écran et de la composition hybride.

## 6. Preuves exécutées

Run GitHub Actions **35409450428**, job **105805933920** : sources préparées depuis `5d6f113ccde76f873de9b6423d6a2748255dc7f1`, validées, puis publiées en `9370a330e98434ae75526c222736db0048d9975e`.

| Contrôle | Résultat |
|---|---|
| Compilation Release du mod et du véritable projet MSTest contre Vintage Story 1.22.7 | Réussite, zéro erreur, trois avertissements |
| Campagne courte partagée des blocages | 35 tests réussis, aucun ignoré |
| Cible MRT RGB réelle avec contexte OpenGL caché | 1 test réussi |
| GLSL de parcours, oracle rayon/boîte et liaison du shader complet | 4 tests réussis |
| GLSL de visibilité d'eau, métadonnées et contacts diagonaux | 4 tests réussis |
| GLSL des matériaux, GGX, énergie et normales | 6 tests réussis |
| GLSL de transport secondaire | 5 tests réussis |
| Scénarios dans le vrai monde Vintage Story de l'utilisateur | Non exécutés ici |
| Performances sur son GPU / pilote Windows | Non mesurées ici |

**Total : 55 tests ciblés réussis ; ce n'est pas la totalité des tests du dépôt.** Environnement : SDK .NET 10.0.401, client officiel Linux 1.22.7, Mesa llvmpipe LLVM 20.1.2, EGL et Xvfb.

Les cinq tests secondaires exécutent les fonctions de calcul GLSL de production avec des impacts et des visibilités explicitement contrôlés. Les interrogations du monde sont des entrées de test déterministes ; les parcours réels sont qualifiés séparément par les tests de rayons. Aucun de ces tests ne constitue une capture en jeu.

Ils vérifient : invariance d'une source chaude entre les slots 0 et 7, visibilité propre à chaque lampe, indépendance ciel/soleil, absence d'énergie diffuse sur une surface noire et distribution de 4 096 directions cosinus. Une mutation volontaire réintroduit la limite de deux lampes et fait disparaître la source du slot 7 tout en conservant celle du slot 0 : le contrôle sait détecter l'ancien défaut.

Deux obstacles ont été rencontrés puis corrigés avant publication : le banc du mutant écrivait huit éléments dans un tableau que le compilateur avait réduit à deux, et des assertions textuelles protégeaient l'ancienne approximation solaire et le plancher de réflectance. Le premier respecte maintenant la taille active tout en exigeant huit éléments dans la production. Les secondes exigent le nouveau raccordement et sont accompagnées des tests numériques ci-dessus. Aucun seuil d'image n'a été assoupli.

Les trois avertissements de build concernent deux annotations de nullabilité existantes et une référence de commentaire XML ; le build n'est pas présenté comme sans avertissement.

Artefact de preuves : `secondary-transport-5d6f113ccde76f873de9b6423d6a2748255dc7f1`, ID **10573394673**, contenant journaux et rapports de tests. Le workflow temporaire de publication est retiré ; la CI en lecture seule conserve les contrôles sur les fichiers ordinaires du commit.

## 7. Ce qui demeure non validé par les résultats utilisateur

- `held-light` : l'auto-occlusion mesurée et la faible réponse des normales doivent être retestées. L'échec de performance y était déclaré inconclusif par le banc, car la référence sans effet était déjà sous ses planchers.
- `cave-interior` : la fraction chaude de 0,08 % n'est pas déclarée corrigée sur la scène réelle. Les échecs 5,81 ms / 59,8 FPS / 42,6 FPS au 1 % low restent des échecs de performance à traiter, pas des seuils à réduire.
- `exterior-roof` : cible solaire à 1,6 % au lieu de 10 %, masque projeté faible et fuites fines restent ouverts. Les changements de précision géométrique sont pertinents mais insuffisants pour certifier ce scénario. Sa référence de performance était elle aussi insuffisante pour conclure.

Restent également la géométrie animée détaillée des occultants, les attaches lumineuses exactes, la convolution rugueuse complète des réflexions, la reprojection temporelle en mouvement, les autres voies de matériaux, l'eau immergée complète et la qualification CPU/GPU globale.

## 8. Reprise courte des essais sous Windows

Sauvegarder les changements locaux avant mise à jour, conserver les artefacts en échec et définir `VINTAGE_STORY` sur l'installation 1.22.7. Le projet de tests compile aussi le mod et son support de scénarios.

```powershell
git fetch origin
git switch dev/renderer-recovery-20260918
git pull --ff-only
git log -1 --oneline

dotnet restore .\tests\VintageRTX.Test\VintageRTX.Test.csproj --source https://api.nuget.org/v3/index.json
dotnet test .\tests\VintageRTX.Test\VintageRTX.Test.csproj -c Release --no-restore --settings .\tests\blocker-smoke.runsettings --logger "trx;LogFileName=blocker-smoke.trx"
dotnet test .\tests\VintageRTX.Test\VintageRTX.Test.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~RawAlbedoTargetTests" --logger "trx;LogFileName=raw-albedo.trx"
```

Ces commandes ne lancent pas toute la campagne longue en jeu. Commencer par elles, puis vérifier une seule scène à éclairage fixe et le message d'activation :

```text
[VintageRTX] Unlit RGB capture active: opaque attachment 4, paired view depth, ...
```

Redémarrer le jeu après mise à jour, éviter deux copies concurrentes du mod et conserver la même configuration pour la comparaison. Les recettes `tools/recovery` ne doivent pas être exécutées sur les sources déjà mises à jour.
