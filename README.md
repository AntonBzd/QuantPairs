````markdown
## QuantPairs

Framework C#/.NET pour recherche et backtest de stratégies de **pairs trading** :

- Pré-traitement & alignement des séries
- Clustering (PCA + KMeans)
- Tests de cointégration (Engle–Granger)
- Grille de validation (VALID) et sélection
- Backtest **Out-Of-Sample** (OOS) 

---

## Arborescence

```text
QuantPairs/
├── QuantPairs.sln
├── Directory.Build.props
├── global.json
├── .gitignore
├── .editorconfig
│
├── src/
│   ├── QuantPairs.Core/         # Modèles & logique métier pure
│   ├── QuantPairs.Data/         # I/O, readers CSV/Excel, validation schéma
│   ├── QuantPairs.MarketData/   # Alignement, retours, log-prices
│   ├── QuantPairs.Research/     # PCA/KMeans, Engle–Granger, validation
│   ├── QuantPairs.Trading/      # Kalman hedge, backtester, sizing
│   ├── QuantPairs.Cli/          # CLI 
│   └── QuantPairs.App/          # Interface WPF minimaliste
│
└── tests/
    └── QuantPairs.Tests/    
````

---

## Prérequis

* **.NET 9.0 SDK**
* Données de prix au format **CSV** ou **Excel** (voir *Format des données* ci-dessous).

---

## Build

```bash
dotnet restore
dotnet build
```

---

## Interface WPF 

Le projet `QuantPairs.App` fournit une petite interface graphique :

* **Parcourir…** : sélection du dataset (par défaut dans `data/raw`).
* **Exécuter le pipeline** : enchaîne les étapes principales via la CLI.
* **Log d’exécution** : reprend ce qui serait affiché dans le terminal.
* **Exporter en PDF** : génère un rapport minimal (dataset + log).

Lancer l’app :

```bash
dotnet run --project src/QuantPairs.App
```

> L’UI ne change pas la logique du pipeline, elle ne fait qu’appeler le CLI et afficher les logs.

---


## Lancer la CLI

Toutes les commandes se font via le projet `QuantPairs.Cli`.

> Astuce : place-toi à la racine du repo (`QuantPairs/`).

### 1) Résumé des données (sanity check)

Vérifie que ton dataset est bien lu et validé.

```bash
dotnet run --project src/QuantPairs.Cli -- --file data/raw/your_file.csv
```

Cette commande :

* Valide le format (dates, valeurs numériques, trous…)
* Construit les `TimeSeriesFrame`
* Affiche un résumé global (nb de séries, nb de points, erreurs éventuelles, etc.)

---

### 2) Clustering (PCA + KMeans)

```bash
dotnet run --project src/QuantPairs.Cli -- cluster \
  --file data/raw/your_file.csv
# options: [--format csv|excel] [--sheet <n>] [--train-start ISO] [--train-end ISO] [--pcs N] [--k K]
```

Sortie principale :

* `data/processed/clusters_YYYYMMDD_HHmmss.csv`
  (mapping `series,cluster`).

La commande :

1. Fait un split **80% train / 20% test** (automatique si non fourni).
2. Construit la matrice de **log-returns** sur TRAIN.
3. Applique **PCA** puis **KMeans** (auto ou manuel).
4. Affiche la variance expliquée et la composition de chaque cluster.

---

### 3) Cointégration (Engle–Granger)

```bash
dotnet run --project src/QuantPairs.Cli -- coint \
  --file data/raw/your_file.csv \
  --clusters data/processed/clusters_*.csv
# options: [--alpha 0.05|0.10] [--max-lag N] [--format csv|excel] [--sheet <n>]
```

Sortie :

* `data/processed/coint_pairs_YYYYMMDD_HHmmss.csv`

Contenu :

* cluster
* series_y, series_x
* n, adf_stat, beta, alpha, used_lag
* half_life (estimateur AR(1) sur les résidus)
* pass_5, pass_10 (booléens)
* approx_pvalue

La commande :

1. Réutilise un split **80/20** (TRAIN / TEST).
2. Aligne les séries et construit les **log-prices**.
3. Teste **toutes les paires** (dans les clusters) dans les deux sens.
4. Calcule la **half-life** de la stratégie de spread.
5. Résume le % de paires cointégrées (5% / 10%) et affiche le top 10 par cluster.

---

### 4) Validation (grille sur VALID, tri par Sharpe)

```bash
dotnet run --project src/QuantPairs.Cli -- validate \
  --file data/raw/your_file.csv \
  --pairs data/processed/coint_pairs_*.csv \
  --mode both \
  --alpha-level 5 \
  --top-n 50
# options avancées:
#   --hl-min / --hl-max
#   --z-entry "1,1.5,2"
#   --z-exit  "0.5,1"
#   --z-stop  "3,4"
#   --q "1e-7,1e-6"
#   --r "1e-4,1e-3"
#   --ppy N (périodes par an)
```

Sortie :

* `data/processed/validate_all_YYYYMMDD_HHmmss.csv`

Contenu (par config) :

* pair_y, pair_x
* mode (static / kalman)
* z_entry, z_exit, z_stop
* sizing (Fixed / HalfLifeScaled / …)
* sharpe, calmar, max_dd, win_rate, profit_factor, turnover
* alpha, beta, half_life (train)
* q, r (paramètres Kalman éventuels)

La commande :

1. Re-split le dataset en **Train / Valid / OOS** :

   * 80% TRAIN
   * 10% VALID
   * 10% OOS
2. Charge les paires cointégrées.
3. Construit une grille de paramètres (seuils Z, sizing, éventuellement Q/R).
4. Backteste toutes les configs sur la période VALID.
5. Affiche un **top local** pour chaque paire, puis un **top global Sharpe**.

---

### 5) Backtest OOS final

```bash
dotnet run --project src/QuantPairs.Cli -- oos \
  --file data/raw/your_file.csv \
  --validate-csv data/processed/validate_all_*.csv \
  --top 1 \
  --ppy 1638
# options:
#   --top N      : nombre de configs retenues par paire
#   --no-plot    : (flag interne pour désactiver l’export equity)
#   --ppy N      : périodes par an
```

Sorties :

* Résumé console des stratégies OOS :

  * Sharpe, Calmar, Max Drawdown, nombre de trades, Win%, statut (ELITE / STRONG / …)
* Fichier equity :

  * `data/processed/OOS_EQUITY_TOP{N}_YYYYMMDD_HHmmss.csv`

Ce fichier contient :

* Colonne `Date`
* Une colonne par stratégie : `PAIR_Sx.xx` où x.xx = Sharpe OOS.

---

## Format des données

### CSV

* Première colonne : **Date**
* Colonnes suivantes : un ticker par colonne
* Exemple :

```csv
Date,BTCUSD,ETHUSD,LTCUSD
2024-01-01,42000,2300,90
2024-01-02,42150,2350,92
...
```

### Excel

* Même logique que le CSV, mais dans un onglet Excel.
* Par défaut, le reader auto-détecte le format (`--format auto`), sinon forcer via :

  * `--format csv`
  * `--format excel`
* Onglet spécifique via `--sheet`.

---

## Sorties principales (récap)

* `data/processed/clusters_*.csv`
  → mapping série → cluster

* `data/processed/coint_pairs_*.csv`
  → résultats Engle–Granger + half-life + flags 5% / 10%

* `data/processed/validate_all_*.csv`
  → toutes les configs évaluées sur VALID, triables par Sharpe

* `data/processed/OOS_EQUITY_TOP*.csv`
  → top N sharpes

---

## Tests

```bash
dotnet test
```

---
