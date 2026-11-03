# Journal des versions

## 1.1 (en cours)
- Chemins réseau : Ouvrir > « Chemin réseau (UNC)... » (saisie de `\\\\serveur\\partage`, connexion avec un autre compte possible, mot de passe non enregistré).
- Lecteurs réseau mappés (H:...) : plus de blocs « non compté » / « espace libre » (la capacité du volume du serveur faussait tout).
- Pas de test d'existence bloquant sur un chemin UNC ; message clair si le chemin est illisible.
- Sur un partage ou un lecteur réseau, la suppression est définitive (pas de corbeille) : avertissement renforcé, « Non » par défaut.

## 1.0
- Panneau de détails (F4) : arborescence, plus gros fichiers, types de fichiers, âge, recherche par nom (Ctrl+F).
- Export CSV de la vue (dossiers, et fichiers de plus de 10 Mo).
- Réglages par défaut = réglages de l'auteur ; `SpaceLite.ini` n'est plus créé tant qu'on ne change rien
  (et il est supprimé si les réglages redeviennent ceux par défaut).
- Icône propre, fenêtre plus grande par défaut, licence MIT propre et mentions de tiers séparées.

## 0.8
- Disposition par bissection équilibrée (densité, biais), rendu biseauté, 12 modes de couleur, options en 3 onglets.

## 0.1 - 0.7
- Treemap, ouverture de lecteurs et de dossiers, glisser-déposer, info-bulles, Admin, Diagnostic, bloc « non compté ».
