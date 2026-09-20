# État de livraison après le lot diffus

Date : 20 septembre 2026.

Code du moteur : `0c48c89e6b65e9cfafc07afb0f627ebdedf5b6bd`.
Révision du filtre permanent et du compte rendu : `d1514365135d4ee4c0930771697a7e5add967808`.
Ce document ajoute seulement le résultat de la vérification élargie, sans changer le moteur ou les critères.

## Vérification sur les fichiers ordinaires publiés

Le workflow **Installable recovery delivery**, run **35527420423**, job **106121907548**, a recompilé le mod, le projet MSTest et RenderLab contre Vintage Story 1.22.7.

- Compilation Release : réussite.
- Sélection permanente des régressions : **155 tests réussis**, aucun ignoré.
- Allocation/capture albedo réelle, sélection séparée : **1 test réussi**, déjà inclus dans les 155.
- GLSL de production et références numériques : **27 tests réussis**.
- `ProductionShaderCompilesAndRendersHeadlessly` : **échec par dépassement du délai existant de 120 secondes sous Mesa llvmpipe**.
- Création du ZIP installable : **non exécutée**, car cette étape exige la réussite des tests précédents.

Le test RenderLab a produit ses captures et ses mesures de séparation PBR avant l'arrêt, mais n'a pas terminé l'ensemble de son exécution chronométrée. Ces images ne prouvent donc pas que le test est réussi. Le partage du temps entre préparation, compilation du pilote et rendu restant n'a pas été mesuré par phase dans ce run ; ne pas attribuer arbitrairement ce timeout à une seule cause.

L'artefact **10610118701**, `VintageRTX-delivery-evidence-d1514365135d4ee4c0930771697a7e5add967808`, conserve les TRX, journaux et captures synthétiques disponibles. Le pipeline de livraison reste en échec pour cette révision, même si les calculs et régressions ciblées ont réussi. Aucun délai, nombre de frames, définition d'image ni seuil visuel ou de performances n'a été assoupli.

## Portée de la livraison source

Les modifications du moteur sont bien présentes sur `dev/renderer-recovery-20260918` : transport diffus à résolution réduite avec sources dynamiques, HDR séparé de la visibilité, absence de radiance historique périmée, budget de parcours dérivé de la portée et captures liées à une scène GPU stable. Le contrôle numérique de population corrige aussi le faux rejet de huit sources dans le scénario stress.

Ce commit est un **candidat de développement**, pas une release complètement qualifiée. Les 21 scénarios en jeu de l'archive utilisateur n'ont pas été rejoués ici. Leurs autres défauts et les performances sur le matériel du joueur restent ouverts, ainsi que le timeout de ce laboratoire autonome.

Voir `2026-09-20-diffuse-runtime-recovery.md` pour l'analyse de l'archive, les modifications et la qualification ciblée avant publication.
