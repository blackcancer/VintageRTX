# Reprise des artefacts complets : transport diffus dynamique

Date : 20 septembre 2026. Branche : `dev/renderer-recovery-20260918`.

**Code publié : `0c48c89e6b65e9cfafc07afb0f627ebdedf5b6bd`.** Ce document ne déclare pas les scénarios en jeu réussis après correction. Aucun monde Vintage Story n'a été rejoué ici sur le GPU Windows de l'utilisateur.

## 1. Corpus réellement analysé

Archive multipart fournie : `runtime.z01` à `runtime.z08` et `runtime.zip`, réunie pour extraction. Elle contient 21 répertoires de campagne et 21 `runtime-result.json`. Vingt exécutions se terminent avec `EvidenceCollected` et une avec `FatalRuntimeSignature`. Les 21 ont un code de retour 1. Ce corpus est distinct du tableau précédent qui affichait deux réussites.

Les cinq empreintes de `runtime-inputs.json` sont identiques dans les 21 répertoires. L'identité désigne les fichiers sélectionnés avant lancement, pas une attestation du module chargé ni un SHA Git :

| Fichier sélectionné | SHA-256 |
|---|---|
| `VintageRTX.dll` | `e01f9f1d81b49fe423a0a827cb5788f21a7466fa2109d6301faa1bac0479a795` |
| `modinfo.json` | `cbdf91b8711d3db28a7fd117682a3ca067a06ee74ffeec2e18f452a8f6c636ac` |
| `display.frag` | `be1e0c27318bd138315d846e251f46919a5e92d9673937c291db1f7560c3f616` |
| `chunkopaque.fsh` | `49c78b58cc5fd93cdda622d6277e32bb31a59cca71baa37e6bb8bee77b5d4e61` |
| `entityanimated.fsh` | `9ae703fc4dc1027d69979c1e418e1ba030b8de5b36068dcd37ea88c69e5f7f35` |

Les journaux de cette nouvelle campagne confirment l'activation de la capture RGB non éclairée. L'ancien échec du hook Harmony ne doit donc pas être réutilisé comme explication de ces nouveaux résultats.

### Principales causes, sans les amalgamer

- `20260920-153042-cave-interior` : rebond visible 0,00 %, chaud 0,00 %, contraste nul ; normales faibles, p90 0,951 degré, réactivité 8,5 %. Le journal indique le niveau Performance et une source dynamique suivie. La version testée envoyait zéro rayon de rebond à ce niveau ; le cache de blocs ne remplaçait pas le transport de cette source dynamique.
- `20260920-154438-many-lights-stress` : seul échec, token littéral `Dynamic point lights tracked: 3` absent. Le journal contient bien **8** sources suivies. C'est un défaut du contrôle textuel de population, pas la preuve d'une population insuffisante.
- `20260920-154300-lantern-night` : comparaison finale de luminance identique à la référence, MAE 0,0000, et témoins statiques insuffisants. Une génération CPU existait déjà avant la capture finale, donc il serait faux d'affirmer que la capture a nécessairement précédé toute construction de scène. Le nouveau verrou GPU/génération/état stable constitue une correction de cohérence, pas une preuve que cet échec particulier est clos.
- Les sept variantes `render-lab*` : ombre solaire de l'herbe absente de la région cible, avec zéro pixel retenu. Certaines variantes ajoutent un déficit de réflexions ou un dépassement de budget. Le test synthétique RenderLab hors monde ne remplace pas ces exécutions dans le jeu.
- `20260920-162827-vegetation-shadow-map` : erreur fatale de préparation, `Vegetation map camera failed: no loaded, flat, exposed staging patch was found.` Les captures et tokens manquants suivants en découlent ; ils ne constituent pas dix-sept causes graphiques indépendantes.
- `nonstandard-geometry`, `reference-room`, `resize-and-reload`, `third-party-pbr` : ombres absentes dans les diagnostics, avec selon le cas un témoin d'enclume manquant ou un faible payload PBR.
- `rain-wetness` et `sunrise-exterior` : normales trop fortes selon les métriques ; la grotte a le problème inverse. Aucun gain global des normal maps n'est appliqué pour faire passer l'un au détriment des autres.
- `20260920-162921-water-reflection` : vitesse horizontale d'impact mesurée `(0,374; -0,070)` contre `(0,240; -0,080)` demandée, un témoin réfléchi manquant et corps local absent du miroir. Ces contrats restent distincts du rebond diffus.
- Sept scénarios déclarent des échecs de performances : held-light, moving-camera, reference-room, render-lab-balanced, render-lab-quality, render-lab-ultra et resize-and-reload. Ce lot ne convertit pas leurs budgets en réussites.

Les journaux utilisateurs complets, paramètres privés, captures et chemins absolus ne sont pas ajoutés au dépôt public. Le présent relevé est limité aux métriques et identités nécessaires à la traçabilité.

## 2. Modification du moteur : rebond réduit, non supprimé

`DiffuseTransportBudget.RayCount` conserve un chemin diffus actif dans tous les niveaux lorsque le nombre demandé et la force de contribution sont positifs. Les niveaux réduits utilisent un échantillon angulaire ; le niveau complet conserve le nombre configuré. Un réglage explicite à zéro ou non fini ne crée pas de transport.

Le calcul est déplacé vers le passage réduit déjà utilisé pour la visibilité des ombres. Il interroge la même table des huit sources sélectionnées, y compris les sources dynamiques. Le niveau Performance ne se contente plus d'un cache construit à partir des seuls blocs lumineux.

### Données et passes

La troisième cible de la banque d'ombres passe de R16F à **RGBA16F** :

- R : visibilité solaire sans dimension, bornée entre 0 et 1.
- GBA : radiance RGB du rebond diffus, linéaire et non bornée artificiellement à 1.

Cela ajoute trois canaux à une cible existante, pas un nouveau sampler. Le stockage supplémentaire concerne le tampon courant et les historiques existants. Les autres deux banques conservent leurs quatre visibilités de sources indépendantes.

1. Passage brut : calcul du rebond sur la grille réduite, avec les sources et occultants du frame courant.
2. Filtre : moyenne spatiale guidée par la géométrie ; seule la visibilité est bornée. Les canaux de radiance n'emploient pas l'ancienne couleur du frame précédent.
3. Résolution finale : reconstruction sur le récepteur compatible. Sans voisin compatible, le pixel utilise sa propre requête, au lieu d'emprunter la lumière d'une autre surface.

Les diagnostics consomment le même rebond résolu que la composition normale. Aucun budget supérieur n'est activé seulement pour les captures. Le mode normal comme le diagnostic restent capables de montrer un véritable zéro lorsque la contribution est désactivée ou absente.

Le nombre de sites du passage brut est d'environ un neuvième des pixels du frame en Performance, et un quart aux autres niveaux, avant les requêtes de repli aux contours. Ce ratio décrit la densité spatiale, **pas un facteur de gain FPS**. Face à l'ancienne suppression complète du rebond, une vraie contribution a nécessairement un coût supplémentaire ; il reste à mesurer sur le matériel réel.

### Portée et invalidation

Le budget de parcours est calculé d'après la distance demandée, `ceil(sqrt(3) * distance) + 3`, borné à 128. Pour un rayon normalisé dans une grille de blocs unitaires, la somme des distances projetées sur les axes ne dépasse pas `sqrt(3) * distance`. Cela évite que l'ancien petit budget de deux à huit cellules annule arbitrairement une portée plus longue. Les états explicites de parcours et les intersections fines sont conservés.

Lorsque le rebond est actif, la passe réduite est actualisée chaque image. Une source éteinte ne réutilise donc pas l'ancienne radiance, même si les textures portent historiquement un nom d'historique. Les occultations temporelles et la radiance gardent des unités et des règles distinctes.

Deux sorties anticipées évitent aussi des parcours inutiles : un soleil réellement éteint ne lance pas la requête solaire d'un impact réfléchi ; une visibilité native déjà nulle n'exige pas de parcours supplémentaire pour calculer son minimum avec la queue voxel.

**Limites maintenues :** huit sources sélectionnées au maximum, surfaces secondaires représentées par les voxels, échantillonnage angulaire sparse, composition hybride héritée et cache statique toujours présents. Ce n'est pas une validation globale de conservation d'énergie de toute la composition ni du path tracing matériel.

## 3. Cohérence des captures

Une comparaison nécessitant les voxels ne commence que lorsque la scène GPU est disponible, la génération publiée est positive et le producteur a terminé ses modifications. La paire baseline/effect conserve la même génération. Une reconstruction ou publication intervenue entre les deux annule la transaction, qui recommence proprement.

Cette barrière ne désactive pas le rendu normal du jeu et ne relève aucun seuil. Les fixtures explicitement sans rendu voxel continuent de tester leur propre capture raster. Les tests vérifient désormais aussi que la capture demandant une scène non publiée reste en attente sans mettre le renderer en défaut.

## 4. Population des lumières du scénario stress

Le validateur extrait un entier complet, pas la sous-chaîne `: 3`. Il exige une population observée entre 3 et 8 inclus dans la table actuelle. Une absence, une population insuffisante, une valeur au-delà de la capacité, un suffixe malformé ou un dépassement numérique restent rejetés. Tous les autres contrôles du scénario restent applicables.

Cette modification retire le faux échec observé pour huit sources, mais ne prétend pas dépasser la limite de huit ni qualifier visuellement une campagne non rejouée.

## 5. Qualification exécutée avant publication

Run GitHub Actions **35527142087**, job **106121166431**, artefact **10609399599**. L'artefact `published-commit.txt` identifie le commit ordinaire `0c48c89e6b65e9cfafc07afb0f627ebdedf5b6bd`.

Compilation Release du mod et du vrai projet MSTest contre le client officiel Vintage Story **1.22.7**, SDK **.NET 10.0.401**, Ubuntu/Mesa/Xvfb : zéro erreur et deux avertissements préexistants (nullabilité de RuntimeScenarioProbe et cref de FilmicDisplayRenderer).

- Campagne précédente complète : **136 exécutés, 136 réussis, aucun ignoré**.
- Classes transport/pipeline/végétation/probe : **31 exécutés, 31 réussis, aucun ignoré**.
- Ces campagnes se recoupent sur douze tests de pipeline : **155 tests C# uniques**, pas 167.
- GLSL réel : parcours 4/4, retours de jeu 4/4, matériaux 9/9, transport 10/10, soit **27 tests réussis**.

Les cinq nouveaux tests C# couvrent les budgets de chaque niveau, 4096 rayons et leurs franchissements de plans, la table de vérité de préparation des captures, la lecture stricte des populations et une véritable allocation RGBA16F relue avec des valeurs supérieures à un.

Les cinq nouveaux tests GLSL font fonctionner le shader de production dans un contexte EGL/Mesa. Une scène synthétique de plafond éclairée seulement par la huitième source produit un rebond chaud HDR, préservé par le filtre. Éteindre la source laisse zéro même avec un poids d'historique de visibilité de 0,93. Une contribution désactivée reste nulle. Le résolveur de géométrie préserve les voisins compatibles et choisit le repli pour une autre profondeur ; ce dernier test emploie des témoins constants explicitement déclarés pour isoler la sélection, pas une fausse implémentation de l'intégrateur.

Les premiers runs ont arrêté la publication sur des fixtures devenues incohérentes ou un uniforme de fixture retiré par le compilateur GLSL. Les tests ont été adaptés à leurs préconditions et au nouveau format HDR ; les critères numériques du rendu n'ont pas été assouplis. L'extinction a été renforcée par un historique effectivement actif.

La publication a vérifié les identités initiales, appliqué le diff explicite, compilé et exécuté les tests, puis poussé uniquement si la branche distante n'avait pas changé. Aucun force-push. Les fichiers ordinaires du moteur contiennent maintenant les modifications. Le staging et les workflows ponctuels de publication/probe ont été retirés. Les workflows permanents restent en lecture seule.

## 6. Réception restant nécessaire

Le lot ne déclare corrigés ni les ombres solaires de l'herbe, ni les faibles payloads PBR, ni les normales de toutes les textures, ni les témoins de corps/eau, ni les mesures GPU de la campagne Windows.

La vérification suivante doit commencer par `many-lights-stress` et `cave-interior`, puis `lantern-night` avec les mêmes réglages. Vérifier la nouvelle contribution réelle et ses performances séparément. Ne pas modifier l'exposition, les seuils ou les surfaces de référence pour imiter l'ancien résultat. Le fichier `runtime-result.json` peut légitimement conserver d'autres erreurs, notamment les normales dans la grotte.

Le filtre permanent `tests/reported-regressions.runsettings` doit conserver les trois classes nouvellement couvertes en plus du corpus précédent. La CI graphique découvre automatiquement `test_reduced_transport.py`. Une suite hors monde verte n'est jamais une réception des 21 mondes sur le matériel du joueur.
