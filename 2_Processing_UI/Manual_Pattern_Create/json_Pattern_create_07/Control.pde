class Control{
  
  void handle(char keyCode){
    if (keyCode == ENTER) {
    if (ONtimefield.isFocus()) {
      ON_time = int(ONtimefield.getText());
      ON_timeText = "ON time is: " + ON_time;
      }
    if (OFFtimefield.isFocus()) {
      OFF_time = int(OFFtimefield.getText());
      OFF_timeText = "OFF time is: " + OFF_time;
      }
    }
  }
  
  
  
}
  
