class MarkElectrodes{
  
  void markElectrodesInsideLoop(ArrayList<PVector> path) {
    int elecs = 0;
    for (int i = 0; i < Electrode_Array.size(); i++) {
      JSONArray row = Electrode_Array.getJSONArray(i);
      for (int j = 0; j < row.size(); j++) {
        int x = j % row.size();
        int y = ((row.size() * i) + j) / row.size();
        PVector electrodePos = new PVector(Electode_Size + x * Electode_Size + (Electode_Size - 30) / 2, 40 + y * Electode_Size + (Electode_Size - 30) / 2);
        if ((isPointInsideLoop(electrodePos, path) && (row.getInt(j) == 1) && flag) || previouslyMarked.contains(electrodePos)) {
          fill(255, 0, 0); // Set color to red
          Elecs_Data[elecs] = 1;
          ellipseMode(CENTER);
          ellipse(electrodePos.x, electrodePos.y, Electode_Size - 30, Electode_Size - 30);
          if (!previouslyMarked.contains(electrodePos)){
            previouslyMarked.add(electrodePos); // Add to previously marked list
          }
        }
        if (row.getInt(j) == 1){
          elecs += 1;
        }
        
      }
    }
  } 
  
  boolean isPointInsideLoop(PVector point, ArrayList<PVector> loop) {
  boolean inside = false;
  for (int i = 0, j = loop.size() - 1; i < loop.size(); j = i++) {
    if ((loop.get(i).y > point.y) != (loop.get(j).y > point.y) &&
      (point.x < (loop.get(j).x - loop.get(i).x) * (point.y - loop.get(i).y) / (loop.get(j).y - loop.get(i).y) + loop.get(i).x)) {
      inside = !inside;
    }
  }
  return inside;
}


}
