# descenders-pathgeneration
path generation script which allows changing the heightmap of a cross slope or flat terrain with control points to change the path types.<br><br><br>
Place RoadPathGenerator.cs into Assets/<br>
Place RoadPathGeneratorEditor.cs into Assets/Editor<br>
goto tools - road path generator - create road generator<br>  this will create a new object in hierarchy with the script assigned and goto inspector tab to define settings<br>
tutorial here - https://youtu.be/adLsvkC-MAI <br><br><br><br>
Gizmo color key <br>
        // ── Colour key ────────────────────────────────────────<br>
        // Cyan        — centreline<br>
        // Red         — road edges<br>
        // Orange      — flatten extra width<br>
        // Yellow      — flatten falloff outer / flatten beyond path outer<br>
        // Green       — berm slope outer<br>
        // Light green — berm outer falloff toe<br>
        // Blue        — ditch inner wall top<br>
        // Cornflower  — ditch floor start<br>
        // Purple      — ditch floor end<br>
        // Magenta     — ditch outer wall toe<br>
        // White       — shoulder smooth outer<br>
        // Pink        — ridge cap zone inner / outer<br>
        // Teal        — curb outer edge<br>
