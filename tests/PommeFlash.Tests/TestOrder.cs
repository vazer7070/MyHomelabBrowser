// Les tests de l'hôte partagent un seul écran X11 (Xvfb) et y simulent clics et touches (XTEST) :
// lancés en parallèle, le clic de l'un tomberait dans la fenêtre de l'autre. Un à la fois.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
