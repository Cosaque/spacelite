# SpaceLite

Analyseur d'espace disque pour Windows : un treemap (carte des fichiers par taille) et un panneau de détails
à la TreeSize. Léger, portable, un seul fichier source, aucune dépendance.

- **Source unique** : `SpaceLite.cs` (C# 5, WinForms). **Compilation** : double-clic sur `build.bat`
  (utilise le `csc.exe` déjà présent dans Windows, .NET Framework 4). Produit `SpaceLite.exe`.
- **Portable** : rien n'est installé. Les réglages vont dans `SpaceLite.ini`, créé **seulement** si vous
  changez les options.

## Utilisation

- **Ouvrir** : lecteurs, puis « Dossier... ». Glisser-déposer d'un dossier, ou `SpaceLite.exe C:\chemin`.
- Clic : sélectionner. Double-clic : entrer dans un dossier. Retour arrière / Zoom - : remonter.
- Zoom tout / + / -, Niveaux -/+ (profondeur affichée), Espace libre, Lancer, Supprimer (corbeille).
- **Détails (F4)** : arborescence avec % du parent, 300 plus gros fichiers, répartition par type, répartition
  par âge, doublons, recherche par nom (Ctrl+F). Colonnes triables, clic droit : Explorateur / copier le chemin.
- **Exporter** : CSV (dossiers et fichiers de plus de 10 Mo) de la vue courante.
- **Admin** : relance en administrateur pour lire les dossiers protégés. **Diagnostic** : écart entre l'espace
  compté et l'espace utilisé du lecteur, liens, dossiers non lus.
- **Options** : affichage, disposition (bissection équilibrée ou squarified, densité, biais), 12 modes de
  couleur (fichiers et dossiers), info-bulles, mémorisation de la fenêtre et du dernier dossier.

## Téléchargement et avertissements Windows

`SpaceLite.exe` est publié dans les « Releases » du dépôt. Il n'est **pas signé numériquement** (une signature
de code coûte plusieurs centaines d'euros par an), donc :

- Windows SmartScreen peut afficher « Windows a protégé votre ordinateur » au premier lancement : cliquez sur
  « Informations complémentaires » puis « Exécuter quand même ».
- Certains antivirus peuvent signaler un programme peu connu (faux positif). Aucune connexion réseau n'est
  faite par SpaceLite ; il ne lit que les noms, tailles et dates des fichiers, et n'écrit rien sauf le
  `SpaceLite.ini` optionnel, l'export CSV que vous demandez et la mise à la corbeille que vous confirmez.
- Le bouton « Admin » relance le programme avec les droits administrateur (demande UAC) pour lire les dossiers protégés.

Pour ne pas avoir à faire confiance à l'exe : le code complet tient dans `SpaceLite.cs`, et `build.bat`
le compile avec le `csc.exe` déjà fourni par Windows (aucun outil à installer). Vous pouvez aussi comparer
l'empreinte SHA-256 de l'exe téléchargé (`certutil -hashfile SpaceLite.exe SHA256`) avec celle indiquée dans
les notes de la release.

## Licence

MIT (`LICENSE`). Des parties dérivent de SpaceMonger 1.x de Sean Werkema (MIT) : voir
`THIRD-PARTY-NOTICES.md` et `LICENSE-SpaceMonger.txt`.
