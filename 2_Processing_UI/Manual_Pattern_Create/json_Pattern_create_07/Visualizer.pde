/*ETactileKit - User defined pattern generation  programme

Visualizer :- used to give visual feedback of the defined pattern

Functions:  visualizeArrayPattern()

*/ 


class Visualizer {
  
  JSONArray electrodeArray;
  int electrodeSize;
  int electrodesNumber;
  int Electode_Size = 80; // Adjust size of electrodes

  Visualizer(JSONArray electrodeArray, int electrodesNumber) {
    this.electrodeArray = electrodeArray;
    this.electrodesNumber = electrodesNumber;
  }

  void visualizeArrayPattern() {
    int elecs = 0;
    while (elecs < Electrode_Array.size()) {
      for (int i = 0; i < Electrode_Array.size(); i++) {
        JSONArray row = Electrode_Array.getJSONArray(i); // Take the relevant pattern
        for (int j = 0; j < row.size(); j++) {
          if (row.getInt(j) == 1) {
            int x = j % row.size();
            int y = ((row.size() * i) + j) / row.size();
            fill(0, 0, 255); // Set color to blue
            fill(0, 0, 255); // Set color to blue
            textSize(20);
            text(elecs, Electode_Size+x*Electode_Size, 38+y*Electode_Size);
            ellipseMode(CENTER);
            ellipse(Electode_Size + x * Electode_Size + (Electode_Size - 30) / 2, 40 + y * Electode_Size + (Electode_Size - 30) / 2, Electode_Size - 30, Electode_Size - 30);
            elecs += 1;
          }
        }
      }
    }
  }
}
