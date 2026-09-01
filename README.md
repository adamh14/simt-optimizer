# SIMT OPTIMIZER

Neoficiální nástroj pro zrychlení hry **Simt Simulator**. Zlepšuje plynulost hry a snižuje nároky na grafickou kartu.

⚠️ **Upozornění:** Nástroj není od autora hry. Používáte ho na vlastní nebezpečí. 

### Jak na to (Krok za krokem)

1. **Vypněte hru.** Během optimalizace nesmí Simt Simulator běžet.
2. **Ověřte si volné místo.** Program vytvoří zálohu původních souborů, potřebujete mít na disku zhruba **2,3 GB volného místa**.
3. Spusťte program dvojklikem na soubor `Optimalizovat.bat`.
4. Počkejte na dokončení procesu. Nic se nemusí instalovat.

> **Hlásí vám to Antivirus?** Nástroj používá PowerShell, což některé antiviry (např. Windows Defender) nemají rády a mohou ho zablokovat. Jde o planý poplach. Pokud se to stane, udělte programu výjimku.

### Jak vše vrátit zpět?
Pokud se po optimalizaci hra chová divně (nebo se vám něco nezdá):
1. Znovu spusťte `Optimalizovat.bat`.
2. Program zjistí, že záloha už existuje, a **nabídne vám vrácení původních souborů**.
3. *Alternativně:* Pokud vrácení nepomůže, jednoduše přeinstalujte hru (program nezasahuje do systémových souborů hry, jen do textur a .ini).

---

# 🛠 Technické detaily (Pro pokročilé)

Níže naleznete detailní informace o tom, jak optimalizátor funguje pod kapotou. Celý zdrojový kód si můžete prohlédnout v souboru `Optimizer.cs`.

### Co program dělá
1. **Vypne debug mód hry:** Hra je vydaná jako debug sestavení, což znamená, že .NET za běhu vypíná optimalizace strojového kódu. Program vedle `SimtSimulator.exe` vytvoří soubor `SimtSimulator.ini`, kterým se optimalizace zapnou zpět. Samotné `.exe` se nijak nemění.
2. **Zkomprimuje vybrané textury:** Část textur hry je uložená nekomprimovaně. Bloková komprese DXT je formát, který grafická karta čte přímo, takže textura zabírá 4x až 8x méně paměti a hlavně méně přenosového pásma. To pomáhá především integrovaným grafikám, které sdílejí paměť s procesorem.
3. **Dogeneruje chybějící mipmapy:** Jen tam, kde je to bezpečné (viz níže).

### Na co program záměrně nesahá
Tohle není opatrnost navíc, každý bod stojí na konkrétní chybě, na kterou se při vývoji narazilo:

*   **Textury s poměrem stran 4:3 a celá složka `Content\Soubory\Modely\Skybox`:** Hra si z nich staví cubemapy tak, že si je načte zpět do paměti jako pole pixelů. Komprimovanou texturu takhle přečíst nelze a hra by spadla.
*   **Mipmapy u textur, které mají průhlednost:** Mipmapa je zmenšená kopie textury pro pohled z dálky. U stromu vyřezaného průhledností rozhoduje o tom, jestli je vůbec vidět. Přegenerování nebo přidání mipmap způsobí, že vzdálené stromy zmizí a objeví se, až k nim dojedete. Proto:
    *   *textura mipmapy už má* -> překomprimují se ty původní, nic se nepřepočítává
    *   *nemá je a je neprůhledná* -> mipmapy se dogenerují
    *   *nemá je a má průhlednost* -> nechá se tak, jak byla
*   **Grafika menu, editoru a palubního počítače:** Na ostrých hranách a písmu je komprese vidět a jde o zanedbatelné množství dat.

### Záloha původních dat
Každý soubor se před přepsáním zkopíruje do složky `_zaloha_original_textury` vedle složky s hrou. 
Když program spustíte znovu a zálohu najde, nabídne místo optimalizace vrácení původních souborů. Tím se zároveň smažou soubory `.ini`, takže se hra vrátí i do debug módu. Zálohu si nechte, dokud si hru pořádně nevyzkoušíte. Smazat ji můžete kdykoli ručně.

### Logování a řešení potíží
Vedle programu vzniká soubor `SimtOptimizer.log` s průběhem. Program nikdy nesahá na nic jiného než na soubory `.xnb` ve složce Content a na soubory `.ini` vedle `.exe`.

### Ověřeno na
Simt Simulator verze **1.8.101.0**. Na jiné verzi se program pustí, ale upozorní vás. Pravidla, podle kterých vybírá soubory, se odvozují z obsahu souborů, ne z pevného seznamu, takže by měla platit i pro jiné verze.

### Seznam souborů
*   `Optimalizovat.bat` - spouštěč
*   `Optimizer.ps1` - zavede a spustí program (přes PowerShell)
*   `Optimizer.cs` - veškerý kód (C#), čitelný a upravitelný
*   `README.txt` / `README.md` - tento soubor
*   `SimtOptimizer.log` - vznikne při prvním spuštění
