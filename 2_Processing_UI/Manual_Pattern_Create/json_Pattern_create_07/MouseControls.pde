class MouseControls{
  
  
  void pressed() {
    previousPoint.set(mouseX, mouseY);
    drawnPath.clear(); // Clear the drawn path when mouse is pressed
  }
  
  void dragged() {
    stroke(0); // Set stroke color to black
    strokeWeight(3); // Set stroke weight
    line(previousPoint.x, previousPoint.y, mouseX, mouseY); // Draw a line from previous point to current point
    previousPoint.set(mouseX, mouseY); // Update previous point
    drawnPath.add(new PVector(mouseX, mouseY)); // Add the current point to the drawn path
  }
  
  
  void released() {
    // Check if the drawn path forms a closed loop
    if (isClosedLoop(drawnPath)) {
      flag = true;
    }
    
    else{
      flag = false;
    }
    background(255); // Clear the canvas
    visualizer.visualizeArrayPattern();// Redraw the visualization pattern
    markelectrodes.markElectrodesInsideLoop(drawnPath); // Mark electrodes inside the loop as red
    
    // Print the Electrode_Data array
    println("Electrode_Data:");
    print("[ ");
    for (int i = 0; i < Elecs_Data.length; i++) {
      print(Elecs_Data[i] + " ");
    }
    println("]"); 
  }
  
  boolean isClosedLoop(ArrayList<PVector> path) {
    // Check if the distance between the first and last points is within a threshold
    float threshold = 20; // Adjust threshold as needed
    if (path.size() < 3) return false; // A loop must have at least 3 points
    return path.get(0).dist(path.get(path.size() - 1)) < threshold;
  }


}
