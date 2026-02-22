# descenders-pathgeneration
path generation script which allows curbs, berms, materials, painted path, slope steepness etc.<br><br><br>
Place RoadPathGenerator.cs into Assets/<br>
Place RoadPathGeneratorEditor.cs into Assets/Editor<br>
goto tools - road path generator - create road generator<br>  this will create a new object in hierarchy with the script assigned and goto inspector tab to define settings<br>
I first would click on + Add Point for as many different points in the path you want ie 8-20<br>
Then i drag each point to the position i want on the terrain in sequential order from start to finish of said path<br>
Then define all settings and click generate path<br><br><br>
important: if you export scene you loose access to restore previous terrain always backup data first or instantly clear road to go back to previous terrain settings<br><br>

example before fixing smoothness between drastic elevation changes and fixed the curbs on terrain level, and also if material is not defined to allow painted texture and if defined to fulfill same zone as painted texture  https://youtu.be/dPUqiEfy-a0

