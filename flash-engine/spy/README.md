# Espion NPAPI (diagnostic)

`npPommeEspion.dll` se place entre un navigateur et le vrai module Flash, et note chaque échange
entre eux dans un journal : paramètres de l'élément (`NPP_New`), questions du module au navigateur
(`NPN_GetValue`) et réponses, scripts de la page (`NPN_Evaluate`) et résultats, propriétés lues
sur la page (`window.location`, `navigator`…), chargements, flux, fenêtre, minuteries.

Il sert à comparer **Basilisk** (Pomme Legacy) et le **moteur intégré** sur une même page : le
même module et la même page donnent deux journaux, et la différence montre ce que Basilisk fait
autrement, sans avoir à le deviner.

Ce n'est pas une partie de PommeBrowser : il n'est livré que par la CI (artefact
`espion-flash`), et ne contient aucun code de Flash (il charge le module de l'utilisateur).

## Utilisation (Windows)

1. Fermez PommeBrowser.
2. Dans le dossier de l'artefact : `powershell -ExecutionPolicy Bypass -File .\Espion-Flash.ps1`
   (le vrai module est rangé dans `%LOCALAPPDATA%\PommeBrowser\plugins\espion`, l'espion prend sa
   place ; sa description de version est copiée de celle du module, que Basilisk lit).
3. Ouvrez la page dans **Basilisk** depuis PommeBrowser (moteur intégré désactivé dans les
   paramètres), jusqu'au problème, puis fermez l'onglet.
4. Réactivez le moteur intégré et ouvrez la même page, jusqu'au problème, puis fermez l'onglet.
5. Fermez PommeBrowser et lancez `.\Espion-Flash.ps1 -Retirer` : le module est remis en place et
   les journaux sont copiés sur le Bureau (`Journaux espion Flash`), un par processus
   (`plugin-container.exe` pour Basilisk, `PommeFlashHost.exe` pour le moteur intégré).

## Données masquées

Valeurs des flashvars et des paramètres d'adresse (noms seuls), cookies (noms seuls), corps des
envois (taille et en-têtes seuls) ; les identifiants de connexion ne sont jamais lus. Les scripts
de la page et leurs résultats sont notés (300 caractères au plus) : relisez le journal avant de le
partager.

## Vérification

La CI compile l'espion, relance les tests de l'hôte avec l'espion devant le greffon de test (ils
doivent passer à l'identique), et essaie le script sur la copie d'une DLL système.
